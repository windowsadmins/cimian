using Cimian.Core.Models;

namespace Cimian.Engine.Predicates;

/// <summary>
/// Interface for the predicate evaluation engine
/// Migrated from Go pkg/predicates functionality
/// </summary>
public interface IPredicateEngine
{
    /// <summary>
    /// Evaluates a single conditional item against system facts
    /// </summary>
    /// <param name="item">The conditional item to evaluate</param>
    /// <param name="facts">Current system facts</param>
    /// <returns>True if the condition is met and item should be processed</returns>
    Task<bool> EvaluateAsync(ConditionalItem item, SystemFacts facts);

    /// <summary>
    /// Evaluates an entire manifest and returns items that should be installed
    /// </summary>
    /// <param name="manifest">The manifest to evaluate</param>
    /// <param name="facts">Current system facts</param>
    /// <returns>Evaluation result with matching items</returns>
    Task<ConditionalEvaluationResult> EvaluateManifestAsync(Manifest manifest, SystemFacts facts);

    /// <summary>
    /// Evaluates a condition expression against system facts
    /// </summary>
    /// <param name="condition">The condition expression to evaluate</param>
    /// <param name="facts">Current system facts</param>
    /// <returns>True if the condition evaluates to true</returns>
    Task<bool> EvaluateConditionAsync(string condition, SystemFacts facts);
}

/// <summary>
/// Result of evaluating a manifest's conditional items
/// </summary>
public class ConditionalEvaluationResult
{
    /// <summary>
    /// Items that matched their conditions and should be installed
    /// </summary>
    public List<ConditionalItem> MatchingItems { get; set; } = new();

    /// <summary>
    /// Items that were evaluated but did not match
    /// </summary>
    public List<ConditionalItem> NonMatchingItems { get; set; } = new();

    /// <summary>
    /// Any errors encountered during evaluation
    /// </summary>
    public List<string> Errors { get; set; } = new();

    /// <summary>
    /// Detailed evaluation trace for debugging
    /// </summary>
    public List<EvaluationTrace> EvaluationTrace { get; set; } = new();

    /// <summary>
    /// Total number of items evaluated
    /// </summary>
    public int TotalItemsEvaluated => MatchingItems.Count + NonMatchingItems.Count;

    /// <summary>
    /// Whether the evaluation completed successfully without errors
    /// </summary>
    public bool IsSuccessful => Errors.Count == 0;
}

/// <summary>
/// Represents a single step in the evaluation process for debugging
/// </summary>
public class EvaluationTrace
{
    /// <summary>
    /// The conditional item being evaluated
    /// </summary>
    public ConditionalItem Item { get; set; } = null!;

    /// <summary>
    /// The condition expression being evaluated
    /// </summary>
    public string? Condition { get; set; }

    /// <summary>
    /// Result of the evaluation (true/false)
    /// </summary>
    public bool Result { get; set; }

    /// <summary>
    /// Time taken to evaluate in milliseconds
    /// </summary>
    public double EvaluationTimeMs { get; set; }

    /// <summary>
    /// Any error message if evaluation failed
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Parsed expression details for debugging
    /// </summary>
    public string? ParsedExpression { get; set; }
}
