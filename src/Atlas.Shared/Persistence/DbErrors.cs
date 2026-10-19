using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Shared.Persistence;

public static class DbErrors
{
    /// <summary>A unique index refused the row: another request created it first.</summary>
    public static bool IsUniqueViolation(this DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 2601 or 2627 };
}
