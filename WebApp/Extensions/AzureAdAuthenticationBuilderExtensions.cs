﻿using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using System;
using System.Security.Claims;
using System.Threading.Tasks;
using WebApp.Dal;

namespace Microsoft.AspNetCore.Authentication
{
    public static class AzureAdAuthenticationBuilderExtensions
    {
        public static AuthenticationBuilder AddAzureAd(this AuthenticationBuilder builder)
            => builder.AddAzureAd(_ => { });

        public static AuthenticationBuilder AddAzureAd(this AuthenticationBuilder builder, Action<AzureAdOptions> configureOptions)
        {
            builder.Services.Configure(configureOptions);
            builder.Services.AddSingleton<IConfigureOptions<OpenIdConnectOptions>, ConfigureAzureOptions>();
            builder.AddOpenIdConnect();
            return builder;
        }

        public static AuthenticationBuilder AddSessionCookie(this AuthenticationBuilder builder)
        {
            builder.AddCookie(options =>
            {
                options.SessionStore = new MemoryCacheTicketStore();
            });

            return builder;
        }

        private class ConfigureAzureOptions : IConfigureNamedOptions<OpenIdConnectOptions>
        {
            private readonly AzureAdOptions _azureOptions;
            private readonly ITenantRepository _tenantRepository;
            private readonly IUserRepository _userRepository;

            public ConfigureAzureOptions(IOptions<AzureAdOptions> azureOptions, ITenantRepository repository, IUserRepository userRepository)
            {
                _azureOptions = azureOptions.Value;
                _tenantRepository = repository;
                _userRepository = userRepository;
            }

            public void Configure(string name, OpenIdConnectOptions options)
            {
                options.ClientId = _azureOptions.ClientId;
                options.Authority = _azureOptions.Instance;
                options.UseTokenLifetime = true;
                options.CallbackPath = _azureOptions.CallbackPath;
                options.RequireHttpsMetadata = false;
                options.ResponseType = OpenIdConnectResponseType.CodeIdToken;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    // Instead of using the default validation (validating against a single issuer value, as we do in
                    // line of business apps), we inject our own multitenant validation logic
                    ValidateIssuer = false                    
                };

                options.Events = new OpenIdConnectEvents
                {
                    OnTicketReceived = context =>
                    {
                        return Task.CompletedTask;
                    },
                    OnAuthenticationFailed = context =>
                    {
                        context.Response.Redirect("/Error");
                        context.HandleResponse(); // Suppress the exception
                        return Task.CompletedTask;
                    },
                    OnAuthorizationCodeReceived = async context =>
                    {
                        // Acquire a Token for the Graph API and cache it using ADAL.
                        string userId = (context.Principal.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier"))?.Value;
                        var clientCredentials = new ClientCredential(_azureOptions.ClientId, _azureOptions.ClientSecret);
                        var authContext = new AuthenticationContext("https://login.microsoftonline.com/common/", new MemoryTokenCache(userId));
                        var authResult = await authContext.AcquireTokenByAuthorizationCodeAsync(context.ProtocolMessage.Code, new Uri($"{context.Request.Scheme}://{context.Request.Host}/signin-oidc"), clientCredentials, _azureOptions.GraphApiUri);

                        // Notify the OIDC middleware that we already took care of code redemption.
                        context.HandleCodeRedemption(context.ProtocolMessage);
                    },
                    OnTokenValidated = async context =>
                    {
                        var upn = context.Principal.FindFirst(ClaimTypes.Name).Value;
                        var tenantId = Guid.Parse(context.Principal.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid").Value);
                        var tenant = await _tenantRepository.GetByTenantId(tenantId);
                        var user = await _userRepository.GetByUpnAndTenantId(upn, tenantId);

                        // if the tenant was not onboarded, deny access
                        if (null == tenant && null == user)
                        {
                            throw new SecurityTokenValidationException();
                        }
                    }
                };
            }

            public void Configure(OpenIdConnectOptions options)
            {
                Configure(Options.DefaultName, options);
            }
        }
    }
}
