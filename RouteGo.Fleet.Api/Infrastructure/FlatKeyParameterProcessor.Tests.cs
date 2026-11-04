using Amazon.SimpleSystemsManagement.Model;
using RouteGo.Fleet.Api.Infrastructure;

namespace RouteGo.Fleet.Api.Tests.Infrastructure;

public sealed class FlatKeyParameterProcessorTests
{
    private const string ParameterPath = "/routego/prod/fleet/";

    [Fact]
    public void GetKey_KebabCaseName_ReturnsPascalCaseKey()
    {
        // Arrange
        var processor = new FlatKeyParameterProcessor();
        var parameter = new Parameter { Name = "/routego/prod/fleet/trip-generation-days" };

        // Act
        string key = processor.GetKey(parameter, ParameterPath);

        // Assert
        Assert.Equal("TripGenerationDays", key);
    }

    [Fact]
    public void GetKey_SingleWordName_ReturnsCapitalisedKey()
    {
        // Arrange
        var processor = new FlatKeyParameterProcessor();
        var parameter = new Parameter { Name = "/routego/prod/fleet/region" };

        // Act
        string key = processor.GetKey(parameter, ParameterPath);

        // Assert
        Assert.Equal("Region", key);
    }
}
