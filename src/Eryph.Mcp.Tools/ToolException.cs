using Azure;
using Eryph.GuestServices.Client;
using ModelContextProtocol;

namespace Eryph.Mcp.Tools;

// The error categories the repo's expert agents route on: a content error means
// the catlet/fodder definition has to change, a state error means the system
// state (missing resource, stopped catlet, eryph not running...) has to change.
public enum ErrorCategory
{
    Content,
    State,
    Transient,
}

// An McpException is the only exception whose message the MCP SDK passes to the
// model, so every expected failure is surfaced as one, prefixed with its category.
public sealed class ToolException(string message, ErrorCategory category)
    : McpException($"[{category.ToString().ToLowerInvariant()} error] {message}")
{
    public ErrorCategory Category { get; } = category;
}

public static class ToolErrors
{
    public static async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (ToolException)
        {
            throw;
        }
        catch (RequestFailedException ex)
        {
            throw new ToolException(DescribeRequestFailure(ex), CategorizeStatus(ex.Status));
        }
        catch (GuestConnectionException ex)
        {
            throw new ToolException(
                $"Could not reach the catlet's guest services: {ex.Message} "
                + "If the catlet was just started, wait_for_provisioning first. If provisioning never "
                + "completes, an earlier fodder step most likely failed (see get_provisioning_log).",
                ErrorCategory.State);
        }
        catch (HttpRequestException ex)
        {
            throw new ToolException($"The eryph API is not reachable: {ex.Message}", ErrorCategory.Transient);
        }
    }

    private static ErrorCategory CategorizeStatus(int status) => status switch
    {
        400 or 422 => ErrorCategory.Content,
        >= 500 => ErrorCategory.Transient,
        _ => ErrorCategory.State,
    };

    private static string DescribeRequestFailure(RequestFailedException ex)
    {
        // The exception message also carries the status, headers, trace id and
        // raw response body; only its first line is useful to the model.
        var message = ex.Message.ReplaceLineEndings("\n").Split('\n')[0].Trim();
        return ex.Status switch
        {
            401 or 403 => $"The eryph client is not authorized for this request (HTTP {ex.Status}): {message}",
            404 => "Resource not found (HTTP 404). Check the catlet / operation id; the catlet may have been removed.",
            409 => $"Conflict (HTTP 409): {message}",
            _ => $"eryph API request failed (HTTP {ex.Status}): {message}",
        };
    }
}
