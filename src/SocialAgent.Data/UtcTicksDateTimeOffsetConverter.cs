using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace SocialAgent.Data;

/// <summary>
/// Stores a <see cref="DateTimeOffset"/> as its UTC ticks, so SQLite can compare and order it in
/// SQL. Values read back at offset zero, which is what PostgreSQL's <c>timestamptz</c> returns, so
/// SQLite (dev) and PostgreSQL (prod) behave alike.
/// </summary>
/// <remarks>
/// EF's built-in <c>DateTimeOffsetToBinaryConverter</c> is not used: it packs the <em>local</em>
/// clock ticks with the offset, so values in different offsets sort by wall-clock time, not by the
/// instant they happened.
/// </remarks>
public sealed class UtcTicksDateTimeOffsetConverter() : ValueConverter<DateTimeOffset, long>(
    value => value.UtcTicks,
    ticks => new DateTimeOffset(ticks, TimeSpan.Zero));
