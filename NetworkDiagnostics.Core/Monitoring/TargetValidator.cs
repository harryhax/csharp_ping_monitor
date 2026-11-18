using System.Globalization;
using System.Net;

namespace NetworkDiagnostics.Core.Monitoring;

public sealed record TargetValidationResult(bool IsValid, string Normalized, bool IsIPv4Literal, string? Error)
{
    public static TargetValidationResult Invalid(string input, string error) => new(false, input, false, error);
}

/// <summary>Validates ping destinations: strict dotted-quad IPv4 addresses or DNS hostnames.</summary>
public static class TargetValidator
{
    public static TargetValidationResult Validate(string? input)
    {
        var value = input?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return TargetValidationResult.Invalid(value, "Enter an IPv4 address or hostname.");
        }

        if (value.Contains(':'))
        {
            return TargetValidationResult.Invalid(value, "IPv6 addresses and URLs are not supported. Enter an IPv4 address (e.g. 8.8.8.8) or a hostname (e.g. example.com).");
        }

        if (value.All(c => char.IsAsciiDigit(c) || c == '.'))
        {
            return TryParseStrictIPv4(value, out var address, out var error)
                ? ValidateIPv4(address!)
                : TargetValidationResult.Invalid(value, error!);
        }

        return ValidateHostname(value);
    }

    /// <summary>Parses only canonical four-octet dotted-decimal notation (rejects "8.8.8", "010.1.1.1", etc.).</summary>
    public static bool TryParseStrictIPv4(string value, out IPAddress? address, out string? error)
    {
        address = null;
        error = null;
        var parts = value.Split('.');
        if (parts.Length != 4)
        {
            error = "An IPv4 address must have four numbers separated by dots, e.g. 8.8.8.8.";
            return false;
        }

        var bytes = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            var p = parts[i];
            if (p.Length is 0 or > 3 || (p.Length > 1 && p[0] == '0') ||
                !int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n > 255)
            {
                error = $"\"{value}\" is not a valid IPv4 address. Each part must be a number from 0 to 255.";
                return false;
            }

            bytes[i] = (byte)n;
        }

        address = new IPAddress(bytes);
        return true;
    }

    private static TargetValidationResult ValidateIPv4(IPAddress address)
    {
        var b = address.GetAddressBytes();
        var text = address.ToString();
        if (b[0] == 0)
        {
            return TargetValidationResult.Invalid(text, $"{text} is not a usable destination address.");
        }

        if (b[0] >= 224)
        {
            return TargetValidationResult.Invalid(text, $"{text} is a multicast, broadcast or reserved address and cannot be pinged reliably.");
        }

        return new TargetValidationResult(true, text, true, null);
    }

    private static TargetValidationResult ValidateHostname(string value)
    {
        var host = value.TrimEnd('.');
        if (host.Length is 0 or > 253)
        {
            return TargetValidationResult.Invalid(value, "The hostname is empty or too long.");
        }

        foreach (var label in host.Split('.'))
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-' ||
                !label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            {
                return TargetValidationResult.Invalid(value, $"\"{value}\" is not a valid hostname. Use letters, digits, hyphens and dots only, e.g. example.com.");
            }
        }

        // A hostname whose final label is all digits would be ambiguous with an IP address.
        if (host.Split('.')[^1].All(char.IsAsciiDigit))
        {
            return TargetValidationResult.Invalid(value, $"\"{value}\" is not a valid hostname or IPv4 address.");
        }

        return new TargetValidationResult(true, host.ToLowerInvariant(), false, null);
    }
}
