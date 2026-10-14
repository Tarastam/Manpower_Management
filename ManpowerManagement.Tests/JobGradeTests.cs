using Xunit;
using ManpowerManagement.Models;

namespace ManpowerManagement.Tests;

public class JobGradeTests
{
    [Theory]
    [InlineData("59T", true)]
    [InlineData("60T", true)]
    [InlineData(" 65 ", true)]
    [InlineData("100T", true)]
    [InlineData("58T", false)]
    [InlineData("52T", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("T59", false)]
    public void Grade59AndAboveRequiresApprover(string? grade, bool expected) =>
        Assert.Equal(expected, JobGrades.RequiresApprover(grade));
}
