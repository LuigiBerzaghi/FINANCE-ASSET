export default function FundMembers({ data }) {
  if (!data) {
    return <div style={{ color: 'var(--text-muted)', fontSize: 12 }}>Carregando membros...</div>;
  }

  if (!data.teamId) {
    return (
      <div style={{ color: 'var(--text-muted)', fontSize: 12 }}>
        Este fundo nao possui um time vinculado.
      </div>
    );
  }

  return (
    <div>
      <div style={{
        display: 'flex',
        justifyContent: 'space-between',
        alignItems: 'center',
        marginBottom: 12,
        color: 'var(--text-muted)',
        fontSize: 11,
      }}>
        <span>{data.teamName}</span>
        <span>{data.members.length} {data.members.length === 1 ? 'membro' : 'membros'}</span>
      </div>

      {data.members.length === 0 ? (
        <div style={{ color: 'var(--text-muted)', fontSize: 12 }}>
          Nenhum membro vinculado a este time.
        </div>
      ) : (
        <div style={{ overflowX: 'auto' }}>
          <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 12 }}>
            <thead>
              <tr>
                {['Nome', 'Email'].map((header) => (
                  <th key={header} style={{
                    padding: '8px 10px',
                    textAlign: 'left',
                    color: 'var(--text-muted)',
                    borderBottom: '1px solid var(--border)',
                    fontSize: 10,
                    textTransform: 'uppercase',
                    fontWeight: 500,
                  }}>
                    {header}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {data.members.map((member) => (
                <tr key={member.id} style={{ borderBottom: '1px solid var(--border)' }}>
                  <td style={{ padding: '10px', color: 'var(--text)', fontWeight: 600 }}>
                    {member.name}
                  </td>
                  <td style={{ padding: '10px', color: 'var(--text-muted)' }}>
                    {member.email}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
