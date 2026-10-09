using Jularr.Web.Data;
using NpgsqlTypes;

namespace Jularr.Tests;

[TestClass]
public sealed class PaginationTests
{
    [TestMethod]
    [DataRow(1, 25, 0L)]
    [DataRow(2, 25, 25L)]
    [DataRow(3, 25, 50L)]
    [DataRow(1001, 100, 100000L)]
    [DataRow(4001, 25, 100000L)]
    public void PageRequest_ValidPage_ComputesOffset(int page, int pageSize, long expectedOffset)
    {
        var request = new PageRequest(page, pageSize);

        Assert.AreEqual(page, request.Page);
        Assert.AreEqual(pageSize, request.PageSize);
        Assert.AreEqual(expectedOffset, request.Offset);
    }

    [TestMethod]
    public void PageRequest_WithoutArguments_UsesDefaults()
    {
        var request = new PageRequest();

        Assert.AreEqual(1, request.Page);
        Assert.AreEqual(25, request.PageSize);
        Assert.AreEqual(0L, request.Offset);
    }

    [TestMethod]
    [DataRow(0, 25)]
    [DataRow(-1, 25)]
    [DataRow(1, 0)]
    [DataRow(1, -1)]
    [DataRow(1, 101)]
    [DataRow(1002, 100)]
    [DataRow(4002, 25)]
    [DataRow(int.MaxValue, 100)]
    public void PageRequest_InvalidPageOrSize_Throws(int page, int pageSize)
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new PageRequest(page, pageSize));
    }

    [TestMethod]
    public void PageRequest_ToSqlParameters_BindsTypedNamesAndValues()
    {
        var request = new PageRequest(3, 25);

        var parameters = request.ToSqlParameters();

        Assert.AreEqual(2, parameters.Length);
        Assert.AreEqual("PageSize", parameters[0].ParameterName);
        Assert.AreEqual(NpgsqlDbType.Integer, parameters[0].NpgsqlDbType);
        Assert.AreEqual(25, parameters[0].Value);
        Assert.AreEqual("Offset", parameters[1].ParameterName);
        Assert.AreEqual(NpgsqlDbType.Bigint, parameters[1].NpgsqlDbType);
        Assert.AreEqual(50L, parameters[1].Value);
    }

    [TestMethod]
    public void PageRequest_MaximumOffset_RejectsExpensiveDeepPages()
    {
        var request = new PageRequest(1001, 100);

        Assert.AreEqual(PageRequest.MaximumOffset, request.Offset);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new PageRequest(1002, 100));
    }

    [TestMethod]
    public void PageResult_From_PreservesRequestedPage()
    {
        var request = new PageRequest(3, 2);
        var result = PageResult<string>.From(["A", "B"], request, totalCount: 7);

        CollectionAssert.AreEqual(new[] { "A", "B" }, result.Items.ToArray());
        Assert.AreEqual(3, result.Page);
        Assert.AreEqual(2, result.PageSize);
        Assert.AreEqual(7L, result.TotalCount);
        Assert.IsNull(result.HasMore);
    }

    [TestMethod]
    public void PageResult_From_RejectsOverfilledPage()
    {
        var request = new PageRequest(pageSize: 2);

        Assert.ThrowsExactly<ArgumentException>(
            () => PageResult<string>.From(["A", "B", "C"], request));
    }

    [TestMethod]
    public void PageResult_From_RejectsNegativeTotalCount()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => PageResult<string>.From([], new PageRequest(), totalCount: -1));
    }
}
