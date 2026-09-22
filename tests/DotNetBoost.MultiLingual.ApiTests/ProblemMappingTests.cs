using DotNetBoost.MultiLingual.API;
using DotNetBoost.MultiLingual.Core;
using Microsoft.AspNetCore.Http;

namespace DotNetBoost.MultiLingual.ApiTests;

public sealed class ProblemMappingTests
{
    [Fact]
    public void BaseLanguageNotTranslatable_IsAConflict_WithItsOwnTitle()
    {
        var exception = new BaseLanguageNotTranslatableException("Product", "en");

        var problem = MultiLingualProblems.ToProblemDetails(exception);

        Assert.Equal(StatusCodes.Status409Conflict, MultiLingualProblems.StatusCodeFor(exception));
        Assert.Equal(StatusCodes.Status409Conflict, problem.Status);
        Assert.Equal("Base language is not a translation", problem.Title);
        Assert.Equal(exception.Message, problem.Detail);
    }
}
