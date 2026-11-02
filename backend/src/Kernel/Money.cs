namespace Flow4Tech.Kernel;

/// <summary>Money as integer minor units (cents) plus ISO 4217 currency. Never floating point.</summary>
public readonly record struct Money(long Cents, string Currency);
