using Microsoft.Extensions.Logging;
using Cimian.Core.Models;
using System.Text.RegularExpressions;
using System.Diagnostics;

namespace Cimian.Engine.Predicates;

/// <summary>
/// Implementation of the predicate evaluation engine
/// Migrated from Go pkg/predicates/predicates.go and pkg/manifest/manifest.go
/// Supports NSPredicate-style conditional evaluation with complex expressions
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
        await Task.CompletedTask; // Placeholder for potential async operations

        return EvaluateExpression(expression, facts);
    }

    /// <summary>
    /// Recursively evaluates a parsed expression against system facts
    /// </summary>
    private bool EvaluateExpression(ParsedExpression expression, SystemFacts facts)
    {
        return expression switch
        {
            LiteralExpression literal => Convert.ToBoolean(literal.Value),
            ComparisonExpression comparison => EvaluateComparison(comparison, facts),
            LogicalExpression logical => EvaluateLogical(logical, facts),
            NotExpression not => !EvaluateExpression(not.Operand, facts),
            AnyExpression any => EvaluateAny(any, facts),
            _ => throw new NotSupportedException($"Expression type {expression.GetType().Name} not supported")
        };
    }

    private bool EvaluateComparison(ComparisonExpression comparison, SystemFacts facts)
    {
        var leftValue = GetFactValue(comparison.Left, facts);
        var rightValue = comparison.Right;

        return comparison.Operator.ToUpperInvariant() switch
        {
            "==" or "EQUALS" => CompareEquals(leftValue, rightValue),
            "!=" or "NOT_EQUALS" => !CompareEquals(leftValue, rightValue),
            "CONTAINS" => CompareContains(leftValue, rightValue),
            "DOES_NOT_CONTAIN" => !CompareContains(leftValue, rightValue),
            "BEGINSWITH" => CompareBeginsWith(leftValue, rightValue),
            "ENDSWITH" => CompareEndsWith(leftValue, rightValue),
            "LIKE" => CompareLike(leftValue, rightValue),
            "IN" => IsValueInCollection(leftValue, rightValue),
            ">" or "GREATER_THAN" => CompareNumeric(leftValue, rightValue) > 0,
            "<" or "LESS_THAN" => CompareNumeric(leftValue, rightValue) < 0,
            ">=" or "GREATER_THAN_OR_EQUAL" => CompareNumeric(leftValue, rightValue) >= 0,
            "<=" or "LESS_THAN_OR_EQUAL" => CompareNumeric(leftValue, rightValue) <= 0,
            _ => throw new NotSupportedException($"Operator {comparison.Operator} not supported")
        };
    }

    private bool EvaluateLogical(LogicalExpression logical, SystemFacts facts)
    {
        return logical.Operator.ToUpperInvariant() switch
        {
            "AND" => logical.Operands.All(op => EvaluateExpression(op, facts)),
            "OR" => logical.Operands.Any(op => EvaluateExpression(op, facts)),
            _ => throw new NotSupportedException($"Logical operator {logical.Operator} not supported")
        };
    }

    private bool EvaluateAny(AnyExpression any, SystemFacts facts)
    {
        // ANY is used with collection facts (e.g., "ANY catalogs != 'Testing'")
        // Get the collection fact and check if any element matches the condition
        var collectionValue = GetFactValue(any.CollectionKey, facts);
        
        if (collectionValue is IEnumerable<string> strings)
        {
            foreach (var item in strings)
            {
                var comparison = new ComparisonExpression
                {
                    Left = "_item",
                    Operator = any.Operator,
                    Right = any.Value
                };
                
                // Create temporary facts with the current item
                var itemResult = any.Operator.ToUpperInvariant() switch
                {
                    "==" or "EQUALS" => CompareEquals(item, any.Value),
                    "!=" or "NOT_EQUALS" => !CompareEquals(item, any.Value),
                    "CONTAINS" => CompareContains(item, any.Value),
                    _ => false
                };
                
                if (itemResult) return true;
            }
        }
        
        return false;
    }

    private object? GetFactValue(string factName, SystemFacts facts)
    {
        return facts.GetFactValue(factName);
    }

    private bool CompareEquals(object? left, object? right)
    {
        var leftStr = left?.ToString() ?? "";
        var rightStr = right?.ToString() ?? "";
        return string.Equals(leftStr, rightStr, StringComparison.OrdinalIgnoreCase);
    }

    private bool CompareContains(object? factValue, object? conditionValue)
    {
        var factStr = factValue?.ToString()?.ToLowerInvariant() ?? "";
        var conditionStr = conditionValue?.ToString()?.ToLowerInvariant() ?? "";
        return factStr.Contains(conditionStr);
    }

    private bool CompareBeginsWith(object? factValue, object? conditionValue)
    {
        var factStr = factValue?.ToString()?.ToLowerInvariant() ?? "";
        var conditionStr = conditionValue?.ToString()?.ToLowerInvariant() ?? "";
        return factStr.StartsWith(conditionStr);
    }

    private bool CompareEndsWith(object? factValue, object? conditionValue)
    {
        var factStr = factValue?.ToString()?.ToLowerInvariant() ?? "";
        var conditionStr = conditionValue?.ToString()?.ToLowerInvariant() ?? "";
        return factStr.EndsWith(conditionStr);
    }

    private bool CompareLike(object? factValue, object? conditionValue)
    {
        // Simple wildcard implementation - * matches any sequence
        var factStr = factValue?.ToString()?.ToLowerInvariant() ?? "";
        var pattern = conditionValue?.ToString()?.ToLowerInvariant() ?? "";
        
        // Remove wildcards and check if the pattern is contained
        pattern = pattern.Replace("*", "");
        return factStr.Contains(pattern);
    }

    private bool IsValueInCollection(object? value, object? collection)
    {
        var valueStr = value?.ToString() ?? "";
        
        if (collection is IEnumerable<string> items)
        {
            return items.Any(item => string.Equals(item, valueStr, StringComparison.OrdinalIgnoreCase));
        }
        
        if (collection is string str)
        {
            return str.Split(',').Any(item => item.Trim().Equals(valueStr, StringComparison.OrdinalIgnoreCase));
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
/// Recursive descent expression parser for NSPredicate-style conditions
/// Migrated from Go pkg/manifest/manifest.go parseComplexCondition and parseSimpleCondition
/// 
/// Grammar:
/// <code>
///   expression    -&gt; orExpression
///   orExpression  -&gt; andExpression ( "OR" andExpression )*
///   andExpression -&gt; notExpression ( "AND" notExpression )*
///   notExpression -&gt; "NOT" notExpression | primary
///   primary       -&gt; comparison | "(" expression ")" | anyExpression
///   anyExpression -&gt; "ANY" comparison
///   comparison    -&gt; identifier operator value
///   operator      -&gt; "==" | "!=" | "CONTAINS" | "BEGINSWITH" | "ENDSWITH" | "LIKE" | "IN" | "&gt;" | "&lt;" | "&gt;=" | "&lt;="
/// </code>
/// </summary>
public class ExpressionParser
{
    private List<Token> _tokens = new();
    private int _position;

    public ParsedExpression Parse(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return new LiteralExpression { Value = true };
        }

        _tokens = Tokenize(expression);
        _position = 0;

        return ParseOrExpression();
    }

    private ParsedExpression ParseOrExpression()
    {
        var left = ParseAndExpression();

        while (MatchKeyword("OR"))
        {
            var right = ParseAndExpression();
            var logicalExpr = left as LogicalExpression;
            
            if (logicalExpr?.Operator == "OR")
            {
                logicalExpr.Operands.Add(right);
            }
            else
            {
                left = new LogicalExpression
                {
                    Operator = "OR",
                    Operands = new List<ParsedExpression> { left, right }
                };
            }
        }

        return left;
    }

    private ParsedExpression ParseAndExpression()
    {
        var left = ParseNotExpression();

        while (MatchKeyword("AND"))
        {
            var right = ParseNotExpression();
            var logicalExpr = left as LogicalExpression;
            
            if (logicalExpr?.Operator == "AND")
            {
                logicalExpr.Operands.Add(right);
            }
            else
            {
                left = new LogicalExpression
                {
                    Operator = "AND",
                    Operands = new List<ParsedExpression> { left, right }
                };
            }
        }

        return left;
    }

    private ParsedExpression ParseNotExpression()
    {
        if (MatchKeyword("NOT"))
        {
            var operand = ParseNotExpression();
            return new NotExpression { Operand = operand };
        }

        return ParsePrimary();
    }

    private ParsedExpression ParsePrimary()
    {
        // Handle parentheses
        if (Match(TokenType.LeftParen))
        {
            var expr = ParseOrExpression();
            Consume(TokenType.RightParen, "Expected ')' after expression");
            return expr;
        }

        // Handle ANY keyword
        if (MatchKeyword("ANY"))
        {
            return ParseAnyExpression();
        }

        // Must be a comparison
        return ParseComparison();
    }

    private ParsedExpression ParseAnyExpression()
    {
        // ANY collectionKey operator value
        var collectionKey = ConsumeIdentifier("Expected collection key after ANY");
        var op = ConsumeOperator();
        var value = ConsumeValue();

        return new AnyExpression
        {
            CollectionKey = collectionKey,
            Operator = op,
            Value = value
        };
    }

    private ParsedExpression ParseComparison()
    {
        var left = ConsumeIdentifier("Expected identifier");
        var op = ConsumeOperator();
        var right = ConsumeValue();

        return new ComparisonExpression
        {
            Left = left,
            Operator = op,
            Right = right
        };
    }

    private bool Match(TokenType type)
    {
        if (_position >= _tokens.Count) return false;
        if (_tokens[_position].Type != type) return false;
        _position++;
        return true;
    }

    private bool MatchKeyword(string keyword)
    {
        if (_position >= _tokens.Count) return false;
        var token = _tokens[_position];
        if (token.Type != TokenType.Keyword) return false;
        if (!string.Equals(token.Value, keyword, StringComparison.OrdinalIgnoreCase)) return false;
        _position++;
        return true;
    }

    private void Consume(TokenType type, string errorMessage)
    {
        if (_position >= _tokens.Count || _tokens[_position].Type != type)
        {
            throw new ParseException(errorMessage);
        }
        _position++;
    }

    private string ConsumeIdentifier(string errorMessage)
    {
        if (_position >= _tokens.Count || _tokens[_position].Type != TokenType.Identifier)
        {
            throw new ParseException(errorMessage);
        }
        return _tokens[_position++].Value;
    }

    private string ConsumeOperator()
    {
        if (_position >= _tokens.Count || _tokens[_position].Type != TokenType.Operator)
        {
            throw new ParseException("Expected operator");
        }
        return _tokens[_position++].Value;
    }

    private string ConsumeValue()
    {
        if (_position >= _tokens.Count)
        {
            throw new ParseException("Expected value");
        }

        var token = _tokens[_position++];
        if (token.Type == TokenType.String || token.Type == TokenType.Number || token.Type == TokenType.Identifier)
        {
            return token.Value;
        }

        throw new ParseException($"Expected value, got {token.Type}");
    }

    /// <summary>
    /// Tokenizes the input expression into a list of tokens
    /// Handles quoted strings, operators, keywords, and identifiers
    /// </summary>
    private List<Token> Tokenize(string expression)
    {
        var tokens = new List<Token>();
        var i = 0;

        while (i < expression.Length)
        {
            // Skip whitespace
            if (char.IsWhiteSpace(expression[i]))
            {
                i++;
                continue;
            }

            // Handle parentheses
            if (expression[i] == '(')
            {
                tokens.Add(new Token(TokenType.LeftParen, "("));
                i++;
                continue;
            }
            if (expression[i] == ')')
            {
                tokens.Add(new Token(TokenType.RightParen, ")"));
                i++;
                continue;
            }

            // Handle quoted strings
            if (expression[i] == '"' || expression[i] == '\'')
            {
                var quote = expression[i];
                i++;
                var start = i;
                while (i < expression.Length && expression[i] != quote)
                {
                    i++;
                }
                var value = expression.Substring(start, i - start);
                tokens.Add(new Token(TokenType.String, value));
                if (i < expression.Length) i++; // Skip closing quote
                continue;
            }

            // Handle multi-character operators
            if (i + 1 < expression.Length)
            {
                var twoChar = expression.Substring(i, 2);
                if (twoChar is "==" or "!=" or ">=" or "<=")
                {
                    tokens.Add(new Token(TokenType.Operator, twoChar));
                    i += 2;
                    continue;
                }
            }

            // Handle single-character operators
            if (expression[i] is '>' or '<')
            {
                tokens.Add(new Token(TokenType.Operator, expression[i].ToString()));
                i++;
                continue;
            }

            // Handle identifiers and keywords
            if (char.IsLetterOrDigit(expression[i]) || expression[i] == '_')
            {
                var start = i;
                while (i < expression.Length && (char.IsLetterOrDigit(expression[i]) || expression[i] == '_'))
                {
                    i++;
                }
                var value = expression.Substring(start, i - start);
                var upperValue = value.ToUpperInvariant();

                // Check if it's a keyword
                if (upperValue is "AND" or "OR" or "NOT" or "ANY")
                {
                    tokens.Add(new Token(TokenType.Keyword, upperValue));
                }
                // Check if it's an operator
                else if (upperValue is "CONTAINS" or "BEGINSWITH" or "ENDSWITH" or "LIKE" or "IN" 
                         or "EQUALS" or "NOT_EQUALS" or "GREATER_THAN" or "LESS_THAN" 
                         or "GREATER_THAN_OR_EQUAL" or "LESS_THAN_OR_EQUAL" or "DOES_NOT_CONTAIN")
                {
                    tokens.Add(new Token(TokenType.Operator, upperValue));
                }
                // Check if it's a number
                else if (double.TryParse(value, out _))
                {
                    tokens.Add(new Token(TokenType.Number, value));
                }
                else
                {
                    tokens.Add(new Token(TokenType.Identifier, value));
                }
                continue;
            }

            // Skip unknown characters
            i++;
        }

        return tokens;
    }
}

public enum TokenType
{
    Identifier,
    Operator,
    Keyword,
    String,
    Number,
    LeftParen,
    RightParen
}

public record Token(TokenType Type, string Value);

public class ParseException : Exception
{
    public ParseException(string message) : base(message) { }
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
/// Logical expression (AND, OR)
/// </summary>
public class LogicalExpression : ParsedExpression
{
    public string Operator { get; set; } = string.Empty;
    public List<ParsedExpression> Operands { get; set; } = new();
}

/// <summary>
/// NOT expression
/// </summary>
public class NotExpression : ParsedExpression
{
    public ParsedExpression Operand { get; set; } = null!;
}

/// <summary>
/// ANY expression for collection matching
/// e.g., "ANY catalogs != 'Testing'"
/// </summary>
public class AnyExpression : ParsedExpression
{
    public string CollectionKey { get; set; } = string.Empty;
    public string Operator { get; set; } = string.Empty;
    public object? Value { get; set; }
}
