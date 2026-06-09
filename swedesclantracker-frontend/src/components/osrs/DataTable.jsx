import { EmptyFeatureState } from "./EmptyFeatureState";

export function DataTable({ columns, rows, getRowKey, getRowClassName, emptyTitle, emptyMessage, caption, footer, className = "" }) {
  const safeRows = Array.isArray(rows) ? rows : [];
  const safeColumns = Array.isArray(columns) ? columns : [];

  return (
    <div className={["osrs-table-wrap", className].filter(Boolean).join(" ")}>
      <table className="osrs-data-table">
        {caption ? <caption>{caption}</caption> : null}
        <thead>
          <tr>
            {safeColumns.map((column) => (
              <th key={column.key ?? column.header}>{column.header}</th>
            ))}
          </tr>
        </thead>
        <tbody>
          {safeRows.length ? safeRows.map((row, index) => (
            <tr
              key={getRowKey ? getRowKey(row, index) : row.id ?? index}
              className={getRowClassName ? getRowClassName(row, index) : undefined}
            >
              {safeColumns.map((column) => (
                <td key={column.key ?? column.header}>
                  {column.render ? column.render(row, index) : row[column.key]}
                </td>
              ))}
            </tr>
          )) : (
            <tr>
              <td colSpan={Math.max(safeColumns.length, 1)}>
                <EmptyFeatureState title={emptyTitle} message={emptyMessage} />
              </td>
            </tr>
          )}
        </tbody>
      </table>
      {footer ? <div className="osrs-table-footer">{footer}</div> : null}
    </div>
  );
}
