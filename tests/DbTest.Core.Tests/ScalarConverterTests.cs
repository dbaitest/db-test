using DbTest.Core.Engine;

namespace DbTest.Core.Tests;

[TestClass]
public sealed class ScalarConverterTests
{
    private enum Status
    {
        Draft = 0,
        Placed = 1,
    }

    [TestMethod]
    public void Null_And_DbNull_Give_Default()
    {
        Assert.IsNull(ScalarConverter.Convert<int?>(null));
        Assert.IsNull(ScalarConverter.Convert<int?>(DBNull.Value));
        Assert.AreEqual(0, ScalarConverter.Convert<int>(DBNull.Value));
        Assert.IsNull(ScalarConverter.Convert<string>(DBNull.Value));
    }

    [TestMethod]
    public void Converts_To_A_Nullable_Target()
    {
        Assert.AreEqual(5, ScalarConverter.Convert<int?>(5L));
        Assert.AreEqual(5L, ScalarConverter.Convert<long?>(5));
    }

    [TestMethod]
    public void Narrows_And_Widens_Numbers()
    {
        Assert.AreEqual(42, ScalarConverter.Convert<int>(42L));
        Assert.AreEqual(42m, ScalarConverter.Convert<decimal>(42));
        Assert.IsTrue(ScalarConverter.Convert<bool>(1UL), "MySQL returns BIT(1) and TINYINT(1) as numbers.");
    }

    [TestMethod]
    public void Converts_Guid_From_Text_And_Bytes()
    {
        var guid = Guid.NewGuid();

        Assert.AreEqual(guid, ScalarConverter.Convert<Guid>(guid.ToString()));
        Assert.AreEqual(guid, ScalarConverter.Convert<Guid?>(guid.ToByteArray(bigEndian: true)));
        Assert.AreEqual(guid, ScalarConverter.Convert<Guid>(guid));
    }

    [TestMethod]
    public void Reads_Binary_Guids_In_The_Rfc_Layout_Mysql_Uses()
    {
        // SELECT UUID_TO_BIN('6ccd780c-baba-1026-9564-5b8c656024db') returns these bytes.
        byte[] bytes = [0x6c, 0xcd, 0x78, 0x0c, 0xba, 0xba, 0x10, 0x26, 0x95, 0x64, 0x5b, 0x8c, 0x65, 0x60, 0x24, 0xdb];

        Assert.AreEqual(Guid.Parse("6ccd780c-baba-1026-9564-5b8c656024db"), ScalarConverter.Convert<Guid>(bytes));
    }

    [TestMethod]
    public void Converts_DateTime_To_DateTimeOffset()
    {
        var unspecified = new DateTime(2026, 11, 8, 10, 30, 0, DateTimeKind.Unspecified);

        var converted = ScalarConverter.Convert<DateTimeOffset>(unspecified);

        Assert.AreEqual(TimeSpan.Zero, converted.Offset, "A database DateTime without a kind is read as UTC.");
        Assert.AreEqual(unspecified.Ticks, converted.UtcTicks);
    }

    [TestMethod]
    public void Converts_Enums_From_Numbers_And_Names()
    {
        Assert.AreEqual(Status.Placed, ScalarConverter.Convert<Status>(1L));
        Assert.AreEqual(Status.Placed, ScalarConverter.Convert<Status?>("placed"));
    }

    [TestMethod]
    public void Rejects_A_Value_It_Cannot_Convert()
    {
        Assert.ThrowsExactly<InvalidCastException>(() => ScalarConverter.Convert<Guid>(12));
    }
}
