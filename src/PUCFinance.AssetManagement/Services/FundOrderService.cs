using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PUCFinance.AssetManagement.Data;
using PUCFinance.AssetManagement.Models;
using PUCFinance.AssetManagement.Models.DTOs;

namespace PUCFinance.AssetManagement.Services;

/// <summary>
/// Aplicacoes e resgates em cotas de fundos (ver FundQuotas). A ordem e registrada com a data da cota
/// (dia da ordem ate as 14h de Brasilia; depois, o proximo dia util) e executada pelo batch, como um trade
/// normal, quando a CVM publica a cota dessa data (~2 dias uteis depois). Ninguem sabe o preco ao dar a
/// ordem, como nos fundos reais. Aplicacao pendente reserva o valor no caixa (compras e novas aplicacoes nao
/// usam esse dinheiro) e conta na exposicao bruta; resgate nunca passa das cotas do fundo (nao ha venda a
/// descoberto de cotas).
/// </summary>
public class FundOrderService
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    private readonly AppDbContext _db;
    private readonly CvmFundsClient _cvm;
    private readonly TradeService _trades;
    private readonly ILogger<FundOrderService> _logger;

    public FundOrderService(AppDbContext db, CvmFundsClient cvm, TradeService trades, ILogger<FundOrderService> logger)
    {
        _db = db;
        _cvm = cvm;
        _trades = trades;
        _logger = logger;
    }

    /// <summary>Registra uma ordem de aplicacao (por valor) ou de resgate (por valor, quantidade ou total).</summary>
    public async Task<FundOrder> CreateAsync(CreateFundOrderRequest request, string? createdBy)
    {
        if (request.Side is not ("long" or "short"))
            throw new ArgumentException("Operacao deve ser 'long' (aplicacao) ou 'short' (resgate)");

        var ticker = request.Ticker?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!FundQuotas.IsFundTicker(ticker))
            throw new ArgumentException("Fundo invalido: escolha o fundo pela busca (FI- + CNPJ da classe)");

        if (!double.IsFinite(request.Amount ?? 0) || !double.IsFinite(request.Quantity ?? 0))
            throw new ArgumentException("Valor ou quantidade invalidos");

        if (await _db.Funds.FindAsync(request.FundId) == null)
            throw new InvalidOperationException($"Fundo {request.FundId} nao encontrado");

        var thesis = string.IsNullOrWhiteSpace(request.Thesis) ? null : request.Thesis.Trim();
        var quoteDate = BrazilTime.ToIsoDate(FundQuotas.QuoteDateFor(DateTimeOffset.UtcNow));

        return request.Side == "long"
            ? await CreateApplicationAsync(request, ticker, thesis, quoteDate, createdBy)
            : await CreateRedemptionAsync(request, ticker, thesis, quoteDate, createdBy);
    }

    private async Task<FundOrder> CreateApplicationAsync(
        CreateFundOrderRequest request, string ticker, string? thesis, string quoteDate, string? createdBy)
    {
        if (request.Quantity is > 0 || request.RedeemAll)
            throw new ArgumentException("Aplicacao e feita por valor (R$): a quantidade de cotas sai da cota do dia");

        var amount = Math.Round(request.Amount ?? 0, 2);
        if (amount < 0.01)
            throw new ArgumentException("Informe o valor da aplicacao (minimo R$ 0,01)");

        var fund = await _cvm.GetFundAsync(ticker)
            ?? throw new InvalidOperationException(
                $"{ticker} nao aceita aplicacoes: fundo fora do cadastro da CVM, encerrado, exclusivo ou fechado");

        // A ultima cota publicada so serve para conferir limites; a ordem vale pela cota do dia
        var lastQuote = await _cvm.GetLatestQuoteAsync(ticker)
            ?? throw new InvalidOperationException(await _cvm.GetLatestDataDateAsync() == null
                ? "Cotas da CVM indisponiveis no momento. Tente novamente em instantes."
                : $"{fund.Name} nao tem cota publicada pela CVM nos ultimos dois meses");

        await EnsureAssetAsync(fund);

        await TradeService.PortfolioLock.WaitAsync();
        try
        {
            var cash = await _db.Cash.FindAsync(request.FundId)
                ?? throw new InvalidOperationException($"Registro de caixa nao encontrado para fundo {request.FundId}");
            await _db.Entry(cash).ReloadAsync();

            var reserved = await _trades.ReservedCashAsync(request.FundId);
            var available = cash.Balance - reserved;
            if (amount > available + 0.005)
            {
                throw new InvalidOperationException(string.Format(PtBr,
                    "Caixa insuficiente. Disponivel: {0:C2}{1}, necessario: {2:C2}",
                    Math.Max(0, available),
                    reserved > 0 ? string.Format(PtBr, " ({0:C2} ja reservado para aplicacoes pendentes)", reserved) : string.Empty,
                    amount));
            }

            // A aplicacao vira posicao comprada na cotizacao: conta na exposicao bruta desde ja
            await _trades.EnsureGrossLimitAsync(request.FundId, ticker, amount / lastQuote.Quota, lastQuote.Quota, cash.Balance);

            var order = new FundOrder
            {
                FundId = request.FundId,
                Ticker = ticker,
                Side = "long",
                Amount = amount,
                QuoteDate = quoteDate,
                Status = FundOrderStatus.Pending,
                Thesis = thesis,
                CreatedBy = createdBy,
            };
            _db.FundOrders.Add(order);
            await _db.SaveChangesAsync();

            _logger.LogInformation(
                "Ordem de aplicacao {OrderId}: R$ {Amount:N2} em {Ticker} ({Name}) pela cota de {QuoteDate} | Fundo {FundId} | {User}",
                order.Id, amount, ticker, fund.Name, quoteDate, request.FundId, createdBy);
            return order;
        }
        finally
        {
            TradeService.PortfolioLock.Release();
        }
    }

    private async Task<FundOrder> CreateRedemptionAsync(
        CreateFundOrderRequest request, string ticker, string? thesis, string quoteDate, string? createdBy)
    {
        if (thesis == null)
            throw new ArgumentException("Informe a justificativa do resgate");

        var modes = (request.RedeemAll ? 1 : 0) + (request.Quantity is > 0 ? 1 : 0) + (request.Amount is > 0 ? 1 : 0);
        if (modes != 1)
            throw new ArgumentException("Informe o resgate por valor (R$), por quantidade de cotas ou total (um dos tres)");

        var amount = request.Amount is > 0 ? Math.Round(request.Amount.Value, 2) : (double?)null;
        var quantity = request.Quantity is > 0 ? Math.Round(request.Quantity.Value, 8) : (double?)null;
        if (amount is < 0.01 || quantity is < FundQuotas.QuantityStep)
            throw new ArgumentException("Valor ou quantidade abaixo do minimo para resgate");

        // Referencia para resgates por valor: ultima cota publicada (o resgate vale pela cota do dia)
        var lastQuote = await _cvm.GetLatestQuoteAsync(ticker);

        await TradeService.PortfolioLock.WaitAsync();
        try
        {
            var position = await _db.Positions.AsNoTracking()
                .FirstOrDefaultAsync(p => p.FundId == request.FundId && p.Ticker == ticker);
            var held = position?.Quantity ?? 0;
            if (held <= 0)
                throw new InvalidOperationException($"O fundo nao tem cotas de {ticker} para resgatar");

            var pending = await _db.FundOrders.AsNoTracking()
                .Where(o => o.FundId == request.FundId && o.Ticker == ticker
                    && o.Status == FundOrderStatus.Pending && o.Side == "short")
                .ToListAsync();
            if (pending.Any(o => o.RedeemAll == 1))
                throw new InvalidOperationException($"Ja existe um resgate total pendente de {ticker}");

            // Cotas ja comprometidas com resgates pendentes (por valor: estimadas na ultima cota)
            var price = lastQuote?.Quota ?? position!.CurrentPrice ?? position!.AvgPrice;
            var committed = pending.Sum(o => o.Quantity ?? (price > 0 ? (o.Amount ?? 0) / price : 0));
            var free = held - committed;
            if (free <= FundQuotas.QuantityStep)
                throw new InvalidOperationException($"Todas as cotas de {ticker} ja estao em resgates pendentes");

            if (quantity > free + 1e-9)
                throw new InvalidOperationException(string.Format(PtBr,
                    "Quantidade maior que as cotas livres para resgate ({0:N8}). Use o resgate total para sair de tudo.", free));

            if (amount > Math.Round(free * price, 2) + 0.005)
                throw new InvalidOperationException(string.Format(PtBr,
                    "Valor maior que as cotas livres para resgate: valem {0:C2} na ultima cota. Use o resgate total para sair de tudo.",
                    free * price));

            var order = new FundOrder
            {
                FundId = request.FundId,
                Ticker = ticker,
                Side = "short",
                Amount = amount,
                Quantity = quantity,
                RedeemAll = request.RedeemAll ? 1 : 0,
                QuoteDate = quoteDate,
                Status = FundOrderStatus.Pending,
                Thesis = thesis,
                CreatedBy = createdBy,
            };
            _db.FundOrders.Add(order);
            await _db.SaveChangesAsync();

            _logger.LogInformation(
                "Ordem de resgate {OrderId}: {What} de {Ticker} pela cota de {QuoteDate} | Fundo {FundId} | {User}",
                order.Id,
                request.RedeemAll ? "total" : amount != null ? $"R$ {amount:N2}" : $"{quantity:N8} cotas",
                ticker, quoteDate, request.FundId, createdBy);
            return order;
        }
        finally
        {
            TradeService.PortfolioLock.Release();
        }
    }

    /// <summary>
    /// Cancela uma ordem pendente. Gestor: ate o horario de corte do dia da cota (depois disso a ordem ja
    /// entrou na cota). Lider: a qualquer momento enquanto pendente (correcao de boleta errada).
    /// </summary>
    public async Task<FundOrder> CancelAsync(int orderId, string? cancelledBy, bool isLeader)
    {
        await TradeService.PortfolioLock.WaitAsync();
        try
        {
            var order = await _db.FundOrders.FindAsync(orderId)
                ?? throw new InvalidOperationException("Ordem nao encontrada");
            await _db.Entry(order).ReloadAsync();

            if (order.Status != FundOrderStatus.Pending)
                throw new InvalidOperationException("So ordens pendentes podem ser canceladas");

            if (!isLeader && DateTimeOffset.UtcNow >= FundQuotas.CancelDeadline(order.QuoteDate))
                throw new InvalidOperationException(string.Format(PtBr,
                    "Prazo de cancelamento encerrado: a ordem ja entrou na cota de {0:dd/MM} (corte as {1}h). So o lider pode cancelar agora.",
                    FundQuotas.ParseDate(order.QuoteDate), FundQuotas.CutoffHour));

            order.Status = FundOrderStatus.Cancelled;
            order.ResolvedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
            order.Message = $"Cancelada por {cancelledBy ?? "usuario"}";
            await _db.SaveChangesAsync();

            _logger.LogInformation("Ordem de fundo {OrderId} cancelada por {User}", orderId, cancelledBy);
            return order;
        }
        finally
        {
            TradeService.PortfolioLock.Release();
        }
    }

    /// <summary>
    /// Executa as ordens pendentes cuja cota ja foi publicada pela CVM (pela primeira cota com data maior ou
    /// igual a da ordem) e recusa as que ficaram sem cota muito tempo. Chamado pelo batch, antes dos precos e
    /// do NAV. Retorna quantas ordens foram executadas.
    /// </summary>
    public async Task<int> ProcessPendingAsync()
    {
        var pending = await _db.FundOrders.AsNoTracking()
            .Where(o => o.Status == FundOrderStatus.Pending)
            .OrderBy(o => o.QuoteDate)
            .ThenBy(o => o.CreatedAt)
            .ThenBy(o => o.Id)
            .ToListAsync();
        if (pending.Count == 0)
            return 0;

        // Cotas (rede) antes de pegar a trava
        var ready = new List<(int OrderId, FundQuote Quote)>();
        var expired = new List<int>();
        var latestData = await _cvm.GetLatestDataDateAsync();
        foreach (var order in pending)
        {
            var quote = await _cvm.GetQuoteOnOrAfterAsync(order.Ticker, order.QuoteDate);
            if (quote != null)
            {
                ready.Add((order.Id, quote));
                continue;
            }

            // A CVM ja publicou cotas bem depois da data da ordem e este fundo nao: parou de informar a cota
            if (latestData != null
                && (FundQuotas.ParseDate(latestData) - FundQuotas.ParseDate(order.QuoteDate)).TotalDays > FundQuotas.ExpireAfterDays)
                expired.Add(order.Id);
        }

        if (ready.Count == 0 && expired.Count == 0)
            return 0;

        await TradeService.PortfolioLock.WaitAsync();
        try
        {
            // Caixa e posicoes lidos de novo dentro da trava (outra operacao pode ter mudado o fundo)
            _db.ChangeTracker.Clear();
            await using var transaction = await _db.Database.BeginTransactionAsync();

            var executed = 0;
            foreach (var (orderId, quote) in ready)
            {
                var order = await _db.FundOrders.FindAsync(orderId);
                if (order is not { Status: FundOrderStatus.Pending })
                    continue;

                if (await ExecuteAsync(order, quote))
                    executed++;
            }

            foreach (var orderId in expired)
            {
                var order = await _db.FundOrders.FindAsync(orderId);
                if (order is not { Status: FundOrderStatus.Pending })
                    continue;

                Reject(order, string.Format(PtBr,
                    "Sem cota publicada pela CVM a partir de {0:dd/MM/yyyy}; ordem recusada e caixa liberado",
                    FundQuotas.ParseDate(order.QuoteDate)));
                await _db.SaveChangesAsync();
            }

            await transaction.CommitAsync();
            return executed;
        }
        catch
        {
            // A transacao foi desfeita: nada pendente pode ser salvo por outra etapa do batch
            _db.ChangeTracker.Clear();
            throw;
        }
        finally
        {
            TradeService.PortfolioLock.Release();
        }
    }

    /// <summary>
    /// Executa a ordem como trade pela cota publicada: aplicacao compra as cotas que o valor paga (arredondadas
    /// para baixo); resgate vende as cotas pedidas (valor: as que o valor paga; o valor da posicao inteira zera),
    /// limitadas ao que o fundo tem. Sem caixa ou sem cotas, a ordem e recusada. Salva a cada ordem.
    /// </summary>
    private async Task<bool> ExecuteAsync(FundOrder order, FundQuote quote)
    {
        var price = quote.Quota;
        var cash = await _db.Cash.FindAsync(order.FundId);
        if (cash == null)
        {
            Reject(order, "Registro de caixa do fundo nao encontrado");
            await _db.SaveChangesAsync();
            return false;
        }

        var held = await _db.Positions
            .Where(p => p.FundId == order.FundId && p.Ticker == order.Ticker)
            .Select(p => (double?)p.Quantity)
            .FirstOrDefaultAsync() ?? 0;

        double quantity;
        if (order.Side == "long")
        {
            quantity = PricingService.QuantityForAmount(order.Amount ?? 0, price, FundQuotas.QuantityStep);
            if (quantity < FundQuotas.QuantityStep)
                return await RejectAsync(order, "Valor menor que a menor fracao de cota");

            if (cash.Balance < quantity * price - 0.005)
                return await RejectAsync(order, string.Format(PtBr,
                    "Caixa insuficiente na cotizacao: disponivel {0:C2}, necessario {1:C2}", cash.Balance, quantity * price));
        }
        else
        {
            if (held <= 0)
                return await RejectAsync(order, $"O fundo nao tem mais cotas de {order.Ticker} para resgatar");

            if (order.RedeemAll == 1)
                quantity = held;
            else if (order.Quantity is > 0)
                quantity = Math.Min(order.Quantity.Value, held);
            else
            {
                // O valor da posicao inteira (em centavos) zera a posicao, como no fechamento por valor
                var amount = order.Amount ?? 0;
                quantity = amount >= Math.Round(held * price, 2) - 0.005
                    ? held
                    : Math.Min(PricingService.QuantityForAmount(amount, price, FundQuotas.QuantityStep), held);
            }

            if (quantity < FundQuotas.QuantityStep)
                return await RejectAsync(order, "Valor menor que a menor fracao de cota");
        }

        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        var trade = new Trade
        {
            FundId = order.FundId,
            Ticker = order.Ticker,
            Side = order.Side,
            Quantity = quantity,
            Price = price,
            Currency = PricingService.BaseCurrency,
            FxRate = 1,
            Thesis = order.Thesis,
            ExecutedAt = now,
            ExecutedBy = order.CreatedBy,
        };
        _db.Trades.Add(trade);

        var signedQuantity = order.Side == "long" ? quantity : -quantity;
        await _trades.UpdatePositionAsync(order.FundId, order.Ticker, signedQuantity, price, order.Side, now);

        cash.Balance -= signedQuantity * price;
        cash.UpdatedAt = now;
        await _db.SaveChangesAsync();

        order.Status = FundOrderStatus.Executed;
        order.ResolvedAt = now;
        order.Price = price;
        order.PriceDate = quote.Date;
        order.TradeId = trade.Id;
        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Ordem de fundo {OrderId} executada: {Side} {Quantity:N8} cotas de {Ticker} a {Price} (cota de {QuoteDate}) | Fundo {FundId}",
            order.Id, order.Side == "long" ? "aplicacao" : "resgate", quantity, order.Ticker, price, quote.Date, order.FundId);
        return true;
    }

    private async Task<bool> RejectAsync(FundOrder order, string message)
    {
        Reject(order, message);
        await _db.SaveChangesAsync();
        return false;
    }

    private void Reject(FundOrder order, string message)
    {
        order.Status = FundOrderStatus.Rejected;
        order.ResolvedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        order.Message = message;
        _logger.LogWarning("Ordem de fundo {OrderId} ({Ticker}, fundo {FundId}) recusada: {Message}",
            order.Id, order.Ticker, order.FundId, message);
    }

    /// <summary>Ordens do fundo: pendentes primeiro (pela cota), depois as mais recentes resolvidas.</summary>
    public async Task<List<FundOrderResponse>> GetOrdersAsync(int fundId, bool isLeader, int resolvedLimit = 30)
    {
        var pending = await _db.FundOrders.AsNoTracking()
            .Where(o => o.FundId == fundId && o.Status == FundOrderStatus.Pending)
            .OrderBy(o => o.QuoteDate).ThenBy(o => o.CreatedAt).ThenBy(o => o.Id)
            .ToListAsync();
        var resolved = await _db.FundOrders.AsNoTracking()
            .Where(o => o.FundId == fundId && o.Status != FundOrderStatus.Pending)
            .OrderByDescending(o => o.ResolvedAt).ThenByDescending(o => o.Id)
            .Take(resolvedLimit)
            .ToListAsync();

        return await ToResponsesAsync(pending.Concat(resolved).ToList(), isLeader);
    }

    public async Task<FundOrderResponse> ToResponseAsync(FundOrder order, bool isLeader) =>
        (await ToResponsesAsync([order], isLeader))[0];

    private async Task<List<FundOrderResponse>> ToResponsesAsync(List<FundOrder> orders, bool isLeader)
    {
        var tickers = orders.Select(o => o.Ticker).Distinct().ToList();
        var names = await _db.Assets.AsNoTracking()
            .Where(a => tickers.Contains(a.Ticker))
            .ToDictionaryAsync(a => a.Ticker, a => a.Name);
        var tradeIds = orders.Where(o => o.TradeId != null).Select(o => o.TradeId!.Value).ToList();
        var tradeQuantities = await _db.Trades.AsNoTracking()
            .Where(t => tradeIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Quantity);

        var now = DateTimeOffset.UtcNow;
        return orders.Select(o =>
        {
            var deadline = FundQuotas.CancelDeadline(o.QuoteDate);
            var pendingOrder = o.Status == FundOrderStatus.Pending;
            return new FundOrderResponse(
                o.Id,
                o.FundId,
                o.Ticker,
                names.GetValueOrDefault(o.Ticker),
                o.Side,
                o.Amount,
                o.Quantity,
                o.RedeemAll == 1,
                o.QuoteDate,
                deadline.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
                pendingOrder && (isLeader || now < deadline),
                o.Status,
                o.Thesis,
                o.CreatedBy,
                o.CreatedAt,
                o.ResolvedAt,
                o.Price,
                o.PriceDate,
                o.TradeId,
                o.TradeId is { } tradeId && tradeQuantities.TryGetValue(tradeId, out var executedQuantity) ? executedQuantity : null,
                o.Message);
        }).ToList();
    }

    /// <summary>
    /// Cadastra o fundo no catalogo de ativos (classe Fundos, setor = classificacao da CVM) para aparecer com
    /// nome na carteira e na exposicao; atualiza nome e classificacao se ja existe.
    /// </summary>
    private async Task EnsureAssetAsync(CvmFund fund)
    {
        var asset = await _db.Assets.FirstOrDefaultAsync(a => a.Ticker == fund.Ticker);
        var isNew = asset == null;
        asset ??= new Asset { Ticker = fund.Ticker };
        asset.Name = fund.Name;
        asset.AssetClass = FundQuotas.AssetClass;
        asset.Sector = fund.Classification;
        asset.Exchange = FundQuotas.Exchange;
        asset.Currency = PricingService.BaseCurrency;
        asset.YahooTicker = null;
        asset.IsActive = 1;

        if (isNew)
            _db.Assets.Add(asset);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Outra ordem cadastrou o mesmo fundo ao mesmo tempo: segue com o cadastro dela
            _db.Entry(asset).State = EntityState.Detached;
        }
    }
}
