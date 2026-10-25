using Microsoft.AspNetCore.Authorization;

namespace FlowPlane.Api.Authentication;

internal sealed class OperatorOptions
{
    public OperatorIdentity[] Allowed { get; init; } = [];

    public void Validate()
    {
        if (Allowed.Length > 100 || Allowed.Any(pair => pair is null ||
            !Uri.TryCreate(pair.Issuer, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") ||
            pair.Issuer.Length > 2048 || pair.Issuer.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(pair.Subject) || pair.Subject.Length > 512 ||
            pair.Subject != pair.Subject.Trim() || pair.Subject.Any(char.IsControl)))
            throw new InvalidOperationException("FlowPlane:Operators:Allowed must contain valid exact issuer/subject pairs.");
    }
}

internal sealed class OperatorIdentity
{
    public string Issuer { get; init; } = "";
    public string Subject { get; init; } = "";
}

internal sealed class OperatorRequirement : IAuthorizationRequirement;

internal sealed class OperatorAuthorizationHandler(OperatorOptions options) : AuthorizationHandler<OperatorRequirement>
{
    public const string Policy = "FlowPlaneOperator";

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, OperatorRequirement requirement)
    {
        foreach (var identity in context.User.Identities.Where(identity => identity.IsAuthenticated))
        {
            var issuers = identity.FindAll("iss").Select(claim => claim.Value).ToArray();
            var subjects = identity.FindAll("sub").Select(claim => claim.Value).ToArray();
            if (issuers.Length == 1 && subjects.Length == 1 && options.Allowed.Any(pair =>
                    string.Equals(pair.Issuer, issuers[0], StringComparison.Ordinal) &&
                    string.Equals(pair.Subject, subjects[0], StringComparison.Ordinal)))
                context.Succeed(requirement);
        }
        return Task.CompletedTask;
    }
}
