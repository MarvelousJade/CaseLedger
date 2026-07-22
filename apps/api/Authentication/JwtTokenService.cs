using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using CaseLedger.Api.Contracts;
using CaseLedger.Api.Domain;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace CaseLedger.Api.Authentication;

public sealed class JwtTokenService(
    IOptions<CaseLedgerAuthenticationOptions> authenticationOptions,
    TimeProvider timeProvider)
{
    private readonly JwtAuthenticationOptions options = authenticationOptions.Value.Jwt;

    public AccessTokenResponse Issue(User user)
    {
        if (!options.Enabled)
        {
            throw new InvalidOperationException("JWT authentication is not enabled.");
        }

        var now = timeProvider.GetUtcNow();
        var expiresAt = now.AddMinutes(options.AccessTokenMinutes);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString("D")),
            new Claim(JwtRegisteredClaimNames.Name, user.Name),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim("role", user.Role),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("D"))
        };
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey!)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            options.Issuer,
            options.Audience,
            claims,
            now.UtcDateTime,
            expiresAt.UtcDateTime,
            credentials);

        return new AccessTokenResponse(
            new JwtSecurityTokenHandler().WriteToken(token),
            expiresAt.UtcDateTime);
    }
}
