namespace PUCFinance.AssetManagement.Services;

/// <summary>Estado de uma posicao: quantidade com sinal (short negativo), preco medio e lado.</summary>
public sealed record PositionState(double Quantity, double AvgPrice, string Side);

/// <summary>Parte de uma posicao encerrada por um trade no sentido oposto.</summary>
public sealed record RealizedFill(double Quantity, double EntryPrice, double ExitPrice, double Pnl, string Side);

/// <summary>
/// Regras de atualizacao de posicao por trade, sem acesso a banco. Usado na execucao de trades,
/// na reconstrucao de posicoes e na reconstrucao do historico de NAV.
/// </summary>
public static class PositionMath
{
    private const double ZeroQuantity = 0.0001;

    /// <summary>
    /// Aplica um trade (quantidade com sinal) a posicao atual. Retorna a nova posicao
    /// (null se zerou) e o P&L realizado, se o trade reduziu ou inverteu a posicao.
    /// </summary>
    public static (PositionState? Position, RealizedFill? Realized) Apply(
        PositionState? current,
        double signedQuantity,
        double price,
        string side)
    {
        if (current == null)
            return (new PositionState(signedQuantity, price, side), null);

        var oldQuantity = current.Quantity;
        var newQuantity = oldQuantity + signedQuantity;
        RealizedFill? realized = null;

        if (Math.Sign(oldQuantity) != Math.Sign(signedQuantity))
        {
            var closedQuantity = Math.Min(Math.Abs(signedQuantity), Math.Abs(oldQuantity));
            var pnl = current.Side == "long"
                ? (price - current.AvgPrice) * closedQuantity
                : (current.AvgPrice - price) * closedQuantity;

            realized = new RealizedFill(closedQuantity, current.AvgPrice, price, pnl, current.Side);
        }

        if (Math.Abs(newQuantity) < ZeroQuantity)
            return (null, realized);

        if (Math.Sign(oldQuantity) == Math.Sign(signedQuantity))
        {
            var avgPrice = ((Math.Abs(oldQuantity) * current.AvgPrice) +
                            (Math.Abs(signedQuantity) * price)) /
                           (Math.Abs(oldQuantity) + Math.Abs(signedQuantity));
            return (current with { Quantity = newQuantity, AvgPrice = avgPrice }, realized);
        }

        if (Math.Sign(newQuantity) == Math.Sign(oldQuantity))
            return (current with { Quantity = newQuantity }, realized);

        return (new PositionState(newQuantity, price, newQuantity > 0 ? "long" : "short"), realized);
    }
}
