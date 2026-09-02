using System.Linq;
using Platform.Web.Services.CodeIntelligence;
using Xunit;

namespace Platform.Tests;

public class RoslynSyntaxIndexerTests
{
    private const string SampleSource = """
        namespace MyApp.Services;

        public class OrderService
        {
            public int OrderCount { get; set; }

            public OrderService() { }

            public void PlaceOrder(string sku)
            {
                // no-op for test
            }
        }

        public interface IOrderRepository
        {
            void Save();
        }
        """;

    [Fact]
    public void IndexSource_FindsRealClassAndMembers()
    {
        var indexer = new RoslynSyntaxIndexer();
        var symbols = indexer.IndexSource(SampleSource);

        Assert.Contains(symbols, s => s.SymbolName == "OrderService" && s.Kind == "Class" && s.Namespace == "MyApp.Services");
        Assert.Contains(symbols, s => s.SymbolName == "PlaceOrder" && s.Kind == "Method" && s.ContainingType == "OrderService");
        Assert.Contains(symbols, s => s.SymbolName == "OrderCount" && s.Kind == "Property" && s.ContainingType == "OrderService");
        Assert.Contains(symbols, s => s.SymbolName == "IOrderRepository" && s.Kind == "Interface");
    }

    [Fact]
    public void IndexSource_LineNumbersAreCorrect()
    {
        var indexer = new RoslynSyntaxIndexer();
        var symbols = indexer.IndexSource(SampleSource);

        var placeOrder = symbols.Single(s => s.SymbolName == "PlaceOrder");
        // Method starts on source line 9 (1-indexed) in SampleSource above.
        Assert.Equal(9, placeOrder.StartLine);
    }
}
