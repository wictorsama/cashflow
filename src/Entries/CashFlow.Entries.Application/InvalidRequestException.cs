namespace CashFlow.Entries.Application;

/// <summary>The request is malformed or out of the allowed limits (not a business rule of the domain).</summary>
public sealed class InvalidRequestException(string message) : Exception(message);
