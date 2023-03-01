using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Duende.IdentityServer.Models;
using IdentityModel;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using DynamicClientRegistrationRequest = Duende.IdentityServer.Configuration.Models.DynamicClientRegistration.DynamicClientRegistrationRequest;

namespace Duende.IdentityServer.Configuration.Validation.DynamicClientRegistration;

public class DynamicClientRegistrationValidator : IDynamicClientRegistrationValidator
{
    private readonly ILogger<DynamicClientRegistrationValidator> _logger;

    public DynamicClientRegistrationValidator(
        ILogger<DynamicClientRegistrationValidator> logger)
    {
        _logger = logger;
    }

    // TODO - Add log messages throughout
    public async Task<DynamicClientRegistrationValidationResult> ValidateAsync(ClaimsPrincipal caller, DynamicClientRegistrationRequest request)
    {
        var client = new Client();

        var result = await SetClientIdAsync(caller, request, client);
        if(result is ValidationStepFailure clientIdStep)
        {
            return clientIdStep.Error;
        }

        result = await SetGrantTypesAsync(caller, request, client);
        if(result is ValidationStepFailure grantTypeValidation)
        {
            return grantTypeValidation.Error;
        }

        result = await SetRedirectUrisAsync(caller, request, client);
        if(result is ValidationStepFailure redirectUrisValidation)
        {
            return redirectUrisValidation.Error;
        }

        result = await SetScopesAsync(caller, request, client);
        if(result is ValidationStepFailure scopeValidation)
        {
            return scopeValidation.Error;
        }
        
        result = await SetSecretsAsync(caller, request, client);
        if(result is ValidationStepFailure keySetValidation)
        {
            return keySetValidation.Error;
        }

        result = await SetClientNameAsync(caller, request, client);
        if(result is ValidationStepFailure nameValidation)
        {
            return nameValidation.Error;
        }

        result = await SetClientUriAsync(caller, request, client);
        if(result is ValidationStepFailure uriValidation)
        {
            return uriValidation.Error;
        }

        result = await SetMaxAgeAsync(caller, request, client);
        if(result is ValidationStepFailure maxAgeValidation)
        {
            return maxAgeValidation.Error;
        }

        result = await ValidateSoftwareStatementAsync(caller, request, client);
        if(result is ValidationStepFailure softwareStatementValidation)
        {
            return softwareStatementValidation.Error;
        }

        return new DynamicClientRegistrationValidatedRequest(client, request);
    }

    protected virtual Task<ValidationStepResult> SetClientIdAsync(ClaimsPrincipal caller,
        DynamicClientRegistrationRequest request,
        Client client)
    {
        client.ClientId = CryptoRandom.CreateUniqueId();
        return Success();
    }


    protected virtual Task<ValidationStepResult> SetGrantTypesAsync(
        ClaimsPrincipal caller,
        DynamicClientRegistrationRequest request,
        Client client)
    {
        if (request.GrantTypes.Count == 0)
        {
            return Failure("grant type is required");
        }

        if (request.GrantTypes.Contains(OidcConstants.GrantTypes.ClientCredentials))
        {
            client.AllowedGrantTypes.Add(GrantType.ClientCredentials);
        }
        if (request.GrantTypes.Contains(OidcConstants.GrantTypes.AuthorizationCode))
        {
            client.AllowedGrantTypes.Add(GrantType.AuthorizationCode);
        }

        // we only support the two above grant types
        if (client.AllowedGrantTypes.Count == 0)
        {
            return Failure("unsupported grant type");
        }

        if (request.GrantTypes.Contains(OidcConstants.GrantTypes.RefreshToken))
        {
            if (client.AllowedGrantTypes.Count == 1 &&
                client.AllowedGrantTypes.FirstOrDefault(t => t.Equals(GrantType.ClientCredentials)) != null)
            {
                return Failure("client credentials does not support refresh tokens");
            }

            client.AllowOfflineAccess = true;
        }

        return Success();
    }

    protected virtual Task<ValidationStepResult> SetRedirectUrisAsync(
        ClaimsPrincipal caller,
        DynamicClientRegistrationRequest request,
        Client client)
    {
        if (client.AllowedGrantTypes.Contains(GrantType.AuthorizationCode))
        {
            if (request.RedirectUris.Any())
            {
                foreach (var requestRedirectUri in request.RedirectUris)
                {
                    if (requestRedirectUri.IsAbsoluteUri)
                    {
                        client.RedirectUris.Add(requestRedirectUri.AbsoluteUri);
                    }
                    else
                    {
                        return Failure("malformed redirect URI", DynamicClientRegistrationErrors.InvalidRedirectUri);
                    }
                }
            }
            else
            {
                // TODO - When/If we implement PAR, this may no longer be an error for clients that use PAR
                return Failure("redirect URI required for authorization_code grant type", DynamicClientRegistrationErrors.InvalidRedirectUri);
            }
        }

        if (client.AllowedGrantTypes.Count == 1 &&
            client.AllowedGrantTypes.FirstOrDefault(t => t.Equals(GrantType.ClientCredentials)) != null)
        {
            if (request.RedirectUris.Any())
            {
                return Failure("redirect URI not compatible with client_credentials grant type", DynamicClientRegistrationErrors.InvalidRedirectUri);
            }
        }

        return Success();
    }

    protected virtual Task<ValidationStepResult> SetScopesAsync(
        ClaimsPrincipal caller,
        DynamicClientRegistrationRequest request,
        Client client)
    {
        if (string.IsNullOrEmpty(request.Scope))
        {
            return SetDefaultScopes(caller, request, client);
        }
        else
        {
            var scopes = request.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if(scopes.Contains("offline_access")) 
            {
                scopes = scopes.Where(s => s != "offline_access").ToArray();
                _logger.LogDebug("offline_access should not be passed as a scope to dynamic client registration. Use the refresh_token grant_type instead.");
            }

            foreach (var scope in scopes)
            {
                client.AllowedScopes.Add(scope);
            }
        }
        return Success();
    }

    protected virtual Task<ValidationStepResult> SetDefaultScopes(ClaimsPrincipal caller, DynamicClientRegistrationRequest request, Client client)
    {
        // This default implementation sets no scopes.
        return Success();
    }

    protected virtual Task<ValidationStepResult> SetSecretsAsync(ClaimsPrincipal caller, DynamicClientRegistrationRequest request, Client client)
    {
        if (request.JwksUri is not null && request.Jwks is not null)
        {
            return Failure("The jwks_uri and jwks parameters must not be used together");
        }

        if (request.Jwks is null && request.TokenEndpointAuthenticationMethod == OidcConstants.EndpointAuthenticationMethods.PrivateKeyJwt)
        {
            return Failure("Missing jwks parameter - the private_key_jwt token_endpoint_auth_method requires the jwks parameter");
        }

        if (request.Jwks is not null && request.TokenEndpointAuthenticationMethod != OidcConstants.EndpointAuthenticationMethods.PrivateKeyJwt)
        {
            return Failure("Invalid authentication method - the jwks parameter requires the private_key_jwt token_endpoint_auth_method");
        }

        if (request.Jwks?.Keys is not null)
        {
            var jsonOptions = new JsonSerializerOptions
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                IgnoreReadOnlyFields = true,
                IgnoreReadOnlyProperties = true,
            };

            foreach (var key in request.Jwks.Keys)
            {
                var jwk = JsonSerializer.Serialize(key, jsonOptions);

                // We parse the jwk to ensure it is valid, but we utlimately
                // write the original text that was passed to us (parsing can
                // change it)
                try
                {
                    var parsedJwk = new IdentityModel.Jwk.JsonWebKey(jwk);

                    // TODO - Other HMAC hashing algorithms would also expect a private key
                    if (parsedJwk.HasPrivateKey && parsedJwk.Alg != SecurityAlgorithms.HmacSha256)
                    {
                        return Failure("unexpected private key in jwk");
                    }
                }
                catch (InvalidOperationException)
                {
                    return Failure("malformed jwk");
                }
                catch (JsonException)
                {
                    return Failure("malformed jwk");
                }

                client.ClientSecrets.Add(new Secret
                {
                    // TODO - Define this constant
                    Type = "JWK", //IdentityServerConstants.SecretTypes.JsonWebKey,
                    Value = jwk
                });
            }
        }
        return Success();
    }


    protected virtual Task<ValidationStepResult> SetClientNameAsync(ClaimsPrincipal caller, DynamicClientRegistrationRequest request, Client client)
    {
        if (!string.IsNullOrWhiteSpace(request.ClientName))
        {
            client.ClientName = request.ClientName;
        }
        return Success();
    }

    protected virtual Task<ValidationStepResult> SetClientUriAsync(ClaimsPrincipal caller, DynamicClientRegistrationRequest request, Client client)
    {
        if (request.ClientUri != null)
        {
            client.ClientUri = request.ClientUri.AbsoluteUri;
        }
        return Success();
    }

    protected virtual Task<ValidationStepResult> SetMaxAgeAsync(ClaimsPrincipal caller, DynamicClientRegistrationRequest request, Client client)
    {
        if (request.DefaultMaxAge.HasValue)
        {
            if(request.DefaultMaxAge <= 0)
            {
                return Failure("default_max_age must be greater than 0 if used");
            }
            client.UserSsoLifetime = request.DefaultMaxAge;
        }
        return Success();
    }

    protected virtual Task<ValidationStepResult> ValidateSoftwareStatementAsync(ClaimsPrincipal caller, DynamicClientRegistrationRequest request, Client client)
    {
        return Success();
    }
    
    protected Task<ValidationStepResult> Failure(string errorDescription, 
        string error = DynamicClientRegistrationErrors.InvalidClientMetadata) =>
            Task.FromResult<ValidationStepResult>(new ValidationStepFailure(
                    error,
                    errorDescription
                ));
    
    protected Task<ValidationStepResult> Success() =>
        Task.FromResult<ValidationStepResult>(new ValidationStepSuccess());

    protected abstract class ValidationStepResult { }

    protected class ValidationStepFailure : ValidationStepResult
    {
        public DynamicClientRegistrationValidationError Error { get; set; }

        public ValidationStepFailure(string error, string errorDescription)
            : this(new DynamicClientRegistrationValidationError(error, errorDescription))
        {
        }

        public ValidationStepFailure(DynamicClientRegistrationValidationError error)
        {
            Error = error;
        }
    }

    protected class ValidationStepSuccess : ValidationStepResult { }
}
