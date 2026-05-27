export default function ThemeToggle({ theme, onToggle }) {
  return (
    <button
      onClick={onToggle}
      aria-label={theme === 'dark' ? 'Mudar para light mode' : 'Mudar para dark mode'}
      style={{
        padding: '6px 10px',
        borderRadius: 4,
        border: '1px solid var(--border)',
        background: 'transparent',
        color: 'var(--text-muted)',
        cursor: 'pointer',
        fontSize: 14,
        lineHeight: 1,
        transition: 'color 0.2s, border-color 0.2s',
      }}
    >
      {theme === 'dark' ? '\u2600' : '\u263E'}
    </button>
  );
}
