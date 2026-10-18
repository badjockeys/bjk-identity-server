using Duende.IdentityServer.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BJK.Idsrv.Host.Pages;

/// <summary>IdentityServer's error page (for example an app asking for an address it isn't allowed to use).</summary>
public sealed class ErrorModel(IIdentityServerInteractionService interaction, IHostEnvironment environment) : PageModel
{
    public string? Message { get; private set; }

    public string? RequestId { get; private set; }

    public async Task OnGetAsync(string? errorId)
    {
        var error = await interaction.GetErrorContextAsync(errorId, HttpContext.RequestAborted);
        if (error is null)
        {
            return;
        }

        RequestId = error.RequestId;
        if (environment.IsDevelopment())
        {
            Message = $"{error.Error}: {error.ErrorDescription}";
        }
    }
}
