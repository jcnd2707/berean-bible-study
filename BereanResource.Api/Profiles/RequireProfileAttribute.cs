using BereanResourceApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace BereanResourceApi.Profiles;

/// <summary>
/// Requires an <c>X-Berean-Profile</c> header naming an existing profile, and returns 400
/// otherwise. This is not an access check — see PROFILES_AND_SESSIONS_PLAN.md D1 — it only keeps
/// personal-data endpoints from being called with no profile selected at all.
/// </summary>
public sealed class RequireProfileAttribute : Attribute, IAsyncActionFilter
{
    public const string HeaderName = "X-Berean-Profile";
    private const string ItemKey = "Berean.ProfileId";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var profileId = context.HttpContext.Request.Headers[HeaderName].ToString();
        var profiles = context.HttpContext.RequestServices.GetRequiredService<ProfileService>();

        if (string.IsNullOrWhiteSpace(profileId) || !profiles.Exists(profileId))
        {
            context.Result = new BadRequestObjectResult(new { error = "No profile selected." });
            return;
        }

        context.HttpContext.Items[ItemKey] = profileId;
        await next();
    }

    internal static string? GetProfileId(HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var id) ? id as string : null;
}

public static class HttpContextProfileExtensions
{
    /// <summary>The profile id validated by <see cref="RequireProfileAttribute"/> for this request.</summary>
    public static string ProfileId(this HttpContext context) =>
        RequireProfileAttribute.GetProfileId(context)
            ?? throw new InvalidOperationException("No profile on this request — add [RequireProfile] to the controller/action.");
}
