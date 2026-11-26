using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SocialAgent.Data.Migrations.Sqlite.Migrations
{
    /// <summary>
    /// Rewrites the DateTimeOffset values that <see cref="StoreDateTimeOffsetAsUtcTicks"/> carried
    /// across as TEXT into the UTC ticks <c>UtcTicksDateTimeOffsetConverter</c> reads. SQLite only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is its own migration because EF cannot order raw SQL relative to the table rebuilds
    /// that SQLite needs for a column type change (it warns when the two share a migration). The
    /// previous migration's rebuild copies every value as it is, and non-numeric text stays text in
    /// an INTEGER-affinity column, so the old values arrive here unchanged.
    /// </para>
    /// <para>
    /// The TEXT form EF wrote is <c>yyyy-MM-dd HH:mm:ss[.FFFFFFF]+HH:MM</c>: the local clock time,
    /// then up to seven fraction digits (none, and no '.', when the fraction is zero), then the
    /// offset. The conversion subtracts the offset, so each value keeps its instant, and values then
    /// read back at offset zero, as they do from PostgreSQL.
    /// </para>
    /// <para>
    /// Each update is guarded by <c>typeof</c>, so it touches only values still in the other form.
    /// That covers a database created by <c>EnsureCreated</c> under the new model and then adopted
    /// as legacy: its columns already hold integers. Nulls are left alone.
    /// </para>
    /// </remarks>
    public partial class ConvertDateTimeOffsetTextToUtcTicks : Migration
    {
        private static readonly (string Table, string Column)[] Columns =
        [
            ("Posts", "CreatedAt"),
            ("Posts", "LastUpdated"),
            ("Notifications", "CreatedAt"),
            ("Profiles", "LastUpdated"),
            ("PollStates", "LastPollTime"),
            ("ProviderTokens", "ExpiresAt"),
            ("ProviderTokens", "UpdatedAt"),
        ];

        /// <summary>Seconds from 0001-01-01 (tick zero) to the Unix epoch.</summary>
        private const long EpochOffsetSeconds = 62135596800;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (table, column) in Columns)
            {
                migrationBuilder.Sql(
                    $"UPDATE \"{table}\" SET \"{column}\" = {TextToUtcTicks($"\"{column}\"")} WHERE typeof(\"{column}\") = 'text';");
            }
        }

        /// <summary>
        /// Back to EF's TEXT form, at offset +00:00 with the instant intact. The previous
        /// migration's Down then rebuilds the columns as TEXT, copying this text as it is.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var (table, column) in Columns)
            {
                migrationBuilder.Sql(
                    $"UPDATE \"{table}\" SET \"{column}\" = {UtcTicksToText($"\"{column}\"")} WHERE typeof(\"{column}\") = 'integer';");
            }
        }

        /// <summary>SQL turning EF's TEXT form of a DateTimeOffset into UTC ticks.</summary>
        private static string TextToUtcTicks(string c)
        {
            // Whole seconds of the local clock time, as if it were UTC.
            var localSeconds = $"CAST(strftime('%s', substr({c}, 1, 19)) AS INTEGER)";

            // The offset in seconds, from the trailing "+HH:MM" or "-HH:MM".
            var offsetSeconds =
                $"((CASE substr({c}, -6, 1) WHEN '-' THEN -1 ELSE 1 END)"
                + $" * (CAST(substr({c}, -5, 2) AS INTEGER) * 3600 + CAST(substr({c}, -2, 2) AS INTEGER) * 60))";

            // The fraction in ticks: up to seven digits, right-padded (".462" is 4620000).
            var fractionTicks =
                $"(CASE WHEN substr({c}, 20, 1) = '.'"
                + $" THEN CAST(substr(substr({c}, 21, length({c}) - 26) || '0000000', 1, 7) AS INTEGER)"
                + " ELSE 0 END)";

            return $"((({localSeconds} - {offsetSeconds} + {EpochOffsetSeconds}) * 10000000) + {fractionTicks})";
        }

        /// <summary>SQL turning UTC ticks back into EF's TEXT form, at offset +00:00.</summary>
        private static string UtcTicksToText(string c)
        {
            // FFFFFFF: seven digits with trailing zeros dropped, and no '.' when nothing is left.
            var fraction = $"rtrim(printf('%07d', {c} % 10000000), '0')";

            return $"(datetime({c} / 10000000 - {EpochOffsetSeconds}, 'unixepoch')"
                + $" || (CASE WHEN {fraction} = '' THEN '' ELSE '.' || {fraction} END)"
                + " || '+00:00')";
        }
    }
}
