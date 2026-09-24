namespace Dle.Domain.Routing;

/// <summary>
/// A single problem found in a rule set by <see cref="RoutingRuleValidator"/>.
/// </summary>
/// <param name="Path">
/// JSON pointer-like location of the offending value in the rule document, using the wire (snake_case)
/// property names — for example <c>rules[2].then.url</c> or <c>rules[0].when.ab[1].percent</c>.
/// The control plane maps this straight onto the RFC 9457 problem details it returns to the API caller.
/// </param>
/// <param name="Message">Human readable explanation, in English, safe to show to an API consumer.</param>
public sealed record RoutingValidationError(string Path, string Message);
