using System.Data;

namespace Platform.Web.Services.Tools;

/// <summary>
/// Deterministic tool. Per architecture rule: never invoke the model for
/// arithmetic — use real computation.
/// </summary>
public class CalculatorTool
{
    public string Name => "CalculatorTool";

    public double Evaluate(string expression)
    {
        // DataTable.Compute is a real, deterministic expression evaluator
        // for arithmetic — no LLM involved.
        var table = new DataTable();
        var result = table.Compute(expression, string.Empty);
        return Convert.ToDouble(result);
    }
}
