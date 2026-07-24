using System.Globalization;
using MapperPillow;
using Xunit;

namespace MapperPillow.Tests;

public class ValueConverterTests
{
    public sealed class CentsToDollars : IValueConverter<int, string>
    {
        public string Convert(int source) => (source / 100m).ToString("0.00", CultureInfo.InvariantCulture);
    }

    public sealed class Product { public int Price { get; set; } }

    public sealed class ProductDto
    {
        [MapConvert(typeof(CentsToDollars))]
        public string Price { get; set; } = "";
    }

    [Fact]
    public void MapConvert_runs_the_custom_converter()
    {
        var product = new Product { Price = 1599 };

        var dto = product.MapTo<ProductDto>();

        Assert.Equal("15.99", dto.Price);
    }
}
