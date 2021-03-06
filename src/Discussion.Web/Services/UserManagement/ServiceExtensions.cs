using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Threading.Tasks;
using Discussion.Core.Communication.Email;
using Discussion.Core.Communication.Sms;
using Discussion.Core.Models.UserAvatar;
using Discussion.Web.Services.UserManagement.Avatar;
using Discussion.Web.Services.UserManagement.Avatar.UrlGenerators;
using Discussion.Web.Services.UserManagement.EmailConfirmation;
using Discussion.Web.Services.UserManagement.Identity;
using Discussion.Web.Services.UserManagement.PhoneNumberVerification;
using IdentityModel;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Discussion.Web.Services.UserManagement
{
    public static class ServiceExtensions
    {
            
        public static void AddUserManagementServices(this IServiceCollection services, IConfiguration appConfiguration)
        {
            services.AddScoped<ICurrentUser, HttpContextCurrentUser>();
            
            services.AddIdentityServices();
            services.AddEmailServices(appConfiguration);
            services.AddSmsServices(appConfiguration);
            
            services.AddSingleton<IAvatarUrlService, DispatchAvatarUrlService>();
            services.AddScoped<IUserAvatarUrlGenerator<DefaultAvatar>, DefaultAvatarUrlGenerator>();
            services.AddScoped<IUserAvatarUrlGenerator<StorageFileAvatar>, StorageFileAvatarUrlGenerator>();
            services.AddScoped<IUserAvatarUrlGenerator<GravatarAvatar>, GravatarAvatarUrlGenerator>();
            
            services.AddScoped<IPhoneNumberVerificationService, DefaultPhoneNumberVerificationService>();
            services.AddSingleton<IConfirmationEmailBuilder, DefaultConfirmationEmailBuilder>();
            services.AddSingleton<IResetPasswordEmailBuilder, DefaultResetPasswordEmailBuilder>();
            
            services.AddScoped<IUserService, DefaultUserService>();
            
            var idpConfig = appConfiguration.GetSection(nameof(ExternalIdentityServiceOptions));
            if (idpConfig != null)
            {
                services.Configure<ExternalIdentityServiceOptions>(idpConfig);
            }

            var parsedConfiguration = idpConfig?.Get<ExternalIdentityServiceOptions>();
            if (parsedConfiguration != null && parsedConfiguration.IsEnabled)
            {
                ConfigureExternalIdp(services, parsedConfiguration);
            }
            services.AddScoped<ExternalSigninManager>();
            services.AddScoped<KeyCloakUserUpdater>();
        }

        private static void ConfigureExternalIdp(IServiceCollection services, ExternalIdentityServiceOptions parsedConfiguration)
        {
            JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();
            
            services.AddAuthentication(options =>
                {
                    options.DefaultScheme = IdentityConstants.ApplicationScheme;
                    options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
                    options.DefaultSignInScheme = IdentityConstants.ApplicationScheme;
                    options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
                })
                .AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, options =>
                {
                    options.CallbackPath = "/oidc-callback";

                    options.Authority = parsedConfiguration.Authority;
                    options.RequireHttpsMetadata = parsedConfiguration.RequireHttpsMetadata;
                    options.ClientId = parsedConfiguration.ClientId;
                    options.ClientSecret = parsedConfiguration.ClientSecret;
                    options.ResponseType = "code id_token";
                    options.GetClaimsFromUserInfoEndpoint = true;

                    options.Scope.Clear();
                    options.Scope.Add(OidcConstants.StandardScopes.OpenId);
                    options.Scope.Add(OidcConstants.StandardScopes.Profile);
                    options.Scope.Add(OidcConstants.StandardScopes.Email);
                    options.Scope.Add(OidcConstants.StandardScopes.Phone);

                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidAudience = parsedConfiguration.ClientId,
                        ValidIssuer =  parsedConfiguration.Authority
                    };
                    options.Events.OnAuthorizationCodeReceived = context =>
                    {
                        var jti = context.JwtSecurityToken.Claims.FirstOrDefault(c => c.Type == JwtClaimTypes.JwtId)?.Value;
                        context.TokenEndpointRequest.SetParameter("client_session_state", jti);   // local user session key id
                        //  context.TokenEndpointRequest.SetParameter("client_session_host", "localhost:8080");  // web server instance name...   https://${application.session.host}.app.com/k_logout
                        return Task.CompletedTask;
                    };
                    options.Events.OnTicketReceived = async context =>
                    {
                        var externalSignOutManager = context.HttpContext.RequestServices.GetService<ExternalSigninManager>();
                        context.Principal = await externalSignOutManager.TransformToDiscussionUser(context.HttpContext, context.Principal.Claims.ToList());
                    };
                    
                    // for Keycloak Back Channel Logout
                    options.RemoteSignOutPath = "/k_logout";
                    options.Events.OnRemoteSignOut = async context =>
                    {
                        var externalSignOutManager = context.HttpContext.RequestServices.GetService<ExternalSigninManager>();
                        await externalSignOutManager.TrySignOutLocalSession(context);
                    };
                });
        }
    }
}