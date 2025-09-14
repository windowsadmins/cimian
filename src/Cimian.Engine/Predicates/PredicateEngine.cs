using Microsoft.Extensions.Logging;
using Cimian.Core.Models;
using System.Text.RegularExpressions;
using System.Diagnostics;

namespace Cimian.Engine.Predicates;

/// <summary>
/// Implementation of the predicate evaluation engine
/// Migrated from Go pkg/predicates/predicates.go
/// </summary>
public class PredicateEngine : IPredicateEngine
{
    private readonly ILogger<PredicateEngine> _logger;
    private readonly ExpressionParser _parser;

    public PredicateEngine(ILogger<PredicateEngine> logger)
    {
        _logger = logger;
        _parser = new ExpressionParser();
    }

    public async Task<bool> EvaluateAsync(ConditionalItem item, SystemFacts facts)
    {
        if (string.IsNullOrWhiteSpace(item.Condition))
        {
            // No condition means always evaluate to true
            return true;
        }

        return await EvaluateConditionAsync(item.Condition, facts);
    }

    public async Task<ConditionalEvaluationResult> EvaluateManifestAsync(Manifest manifest, SystemFacts facts)
    {
        var result = new ConditionalEvaluationResult();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            _logger.LogInformation("Evaluating manifest '{ManifestName}' with {ItemCount} conditional items", 
                manifest.Name, manifest.ConditionalItems.Count);

            await EvaluateConditionalItemsAsync(manifest.ConditionalItems, facts, result);

            _logger.LogInformation("Manifest evaluation completed: {MatchingCount} matching, {NonMatchingCount} non-matching items",
                result.MatchingItems.Count, result.NonMatchingItems.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error evaluating manifest");
            result.Errors.Add($"Manifest evaluation error: {ex.Message}");
        }

        return result;
    }

    public async Task<bool> EvaluateConditionAsync(string condition, SystemFacts facts)
    {
        try
        {
            var expression = _parser.Parse(condition);
            return await EvaluateExpressionAsync(expression, facts);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error evaluating condition: {Condition}", condition);
            return false;
        }
    }

    private async Task EvaluateConditionalItemsAsync(List<ConditionalItem> items, SystemFacts facts, ConditionalEvaluationResult result)
    {
        foreach (var item in items)
        {
            var stopwatch = Stopwatch.StartNew();
            var trace = new EvaluationTrace
            {
                Item = item,
                Condition = item.Condition
            };

            try
            {
                var matches = await EvaluateAsync(item, facts);
                trace.Result = matches;
                trace.EvaluationTimeMs = stopwatch.Elapsed.TotalMilliseconds;

                if (matches)
                {
                    if (item.IsLeafItem)
                    {
                        result.MatchingItems.Add(item);
                        _logger.LogDebug("Conditional item '{Name}' matched condition", item.Name);
                    }
                    
                    // Evaluate nested items if this item matches
                    if (item.HasNestedItems)
                    {
                        await EvaluateConditionalItemsAsync(item.ConditionalItems!, facts, result);
                    }
                }
                else
                {
                    if (item.IsLeafItem)
                    {
                        result.NonMatchingItems.Add(item);
                        _logger.LogDebug("Conditional item '{Name}' did not match condition", item.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error evaluating conditional item");
                trace.ErrorMessage = ex.Message;
                result.Errors.Add($"Error evaluating item: {ex.Message}");
            }

            result.EvaluationTrace.Add(trace);
        }
    }

    private async Task<bool> EvaluateExpressionAsync(ParsedExpression expression, SystemFacts facts)
    {
        // This is a simplified placeholder implementation
        // The full implementation would handle all NSPredicate-style operators
        
        await Task.CompletedTask; // Placeholder for async operations

        return expression switch
        {
            LiteralExpression literal => Convert.ToBoolean(literal.Value),
            ComparisonExpression comparison => EvaluateComparison(comparison, facts),
            LogicalExpression logical => EvaluateLogical(logical, facts),
            _ => throw new NotSupportedException($"Expression type {expression.GetType().Name} not supported")
        };
    }

    private bool EvaluateComparison(ComparisonExpression comparison, SystemFacts facts)
    {
        var leftValue = GetFactValue(comparison.Left, facts);
        var rightValue = comparison.Right;

        return comparison.Operator.ToUpperInvariant() switch
        {
            "==" or "EQUALS" => Equals(leftValue, rightValue),
            "!=" or "NOT EQUALS" => !Equals(leftValue, rightValue),
            "CONTAINS" => leftValue?.ToString()?.Contains(rightValue?.ToString() ?? "", StringComparison.OrdinalIgnoreCase) ?? false,
            "BEGINSWITH" => leftValue?.ToString()?.StartsWith(rightValue?.ToString() ?? "", StringComparison.OrdinalIgnoreCase) ?? false,
            "ENDSWITH" => leftValue?.ToString()?.EndsWith(rightValue?.ToString() ?? "", StringComparison.OrdinalIgnoreCase) ?? false,
            "IN" => IsValueInCollection(leftValue, rightValue),
            ">" => CompareNumeric(leftValue, rightValue) > 0,
            "<" => CompareNumeric(leftValue, rightValue) < 0,
            ">=" => CompareNumeric(leftValue, rightValue) >= 0,
            "<=" => CompareNumeric(leftValue, rightValue) <= 0,
            _ => throw new NotSupportedException($"Operator {comparison.Operator} not supported")
        };
    }

    private bool EvaluateLogical(LogicalExpression logical, SystemFacts facts)
    {
        // Simplified implementation - would need full recursive evaluation
        return logical.Operator.ToUpperInvariant() switch
        {
            "AND" => true, // Placeholder
            "OR" => true,  // Placeholder
            "NOT" => false, // Placeholder
            _ => throw new NotSupportedException($"Logical operator {logical.Operator} not supported")
        };
    }

    private object? GetFactValue(string factName, SystemFacts facts)
    {
        return facts.GetFactValue(factName);
    }

    private bool IsValueInCollection(object? value, object? collection)
    {
        if (collection is string str)
        {
            return str.Split(',').Any(item => item.Trim().Equals(value?.ToString(), StringComparison.OrdinalIgnoreCase));
        }
        
        return false;
    }

    private int CompareNumeric(object? left, object? right)
    {
        if (double.TryParse(left?.ToString(), out var leftNum) && 
            double.TryParse(right?.ToString(), out var rightNum))
        {
            return leftNum.CompareTo(rightNum);
        }
        
        return string.Compare(left?.ToString(), right?.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Simple expression parser for NSPredicate-style conditions
/// This is a simplified implementation - full version would be much more complex
/// </summary>
public class ExpressionParser
{
    public ParsedExpression Parse(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return new LiteralExpression { Value = true };
        }

        // This is a very simplified parser - real implementation would be much more sophisticated
        // Handle simple comparisons for now
        var comparisonOperators = new[] { "==", "!=", "CONTAINS", "BEGINSWITH", "ENDSWITH", "IN", ">=", "<=", ">", "<" };
        
        foreach (var op in comparisonOperators)
        {
            if (expression.Contains(op, StringComparison.OrdinalIgnoreCase))
            {
                var parts = expression.Split(new[] { op }, 2, StringSplitOptions.None);
                if (parts.Length == 2)
                {
                    return new ComparisonExpression
                    {
                        Left = parts[0].Trim(),
                        Operator = op,
                        Right = parts[1].Trim().Trim('"', '\'')
                    };
                }
            }
        }

        // Default to literal true for unparseable expressions
        return new LiteralExpression { Value = true };
    }
}

/// <summary>
/// Base class for parsed expressions
/// </summary>
public abstract class ParsedExpression
{
}

/// <summary>
/// Literal value expression (true, false, numbers, strings)
/// </summary>
public class LiteralExpression : ParsedExpression
{
    public object? Value { get; set; }
}

/// <summary>
/// Comparison expression (left operator right)
/// </summary>
public class ComparisonExpression : ParsedExpression
{
    public string Left { get; set; } = string.Empty;
    public string Operator { get; set; } = string.Empty;
    public object? Right { get; set; }
}

/// <summary>
/// Logical expression (AND, OR, NOT)
/// </summary>
public class LogicalExpression : ParsedExpression
{
    public string Operator { get; set; } = string.Empty;
    public List<ParsedExpression> Operands { get; set; } = new();
}
