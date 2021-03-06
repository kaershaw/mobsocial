﻿using System.Web.Configuration;
using System.Web.Http;
using System.Web.Http.Cors;
using DryIoc.Owin;
using DryIoc.WebApi.Owin;
using mobSocial.Core.Infrastructure.AppEngine;
using Microsoft.Owin;
using Owin;
using mobSocial.WebApi;
using mobSocial.WebApi.Configuration.Middlewares;
using mobSocial.WebApi.Configuration.SignalR.Providers;
using mobSocial.WebApi.Extensions;
using Microsoft.AspNet.SignalR;
using Microsoft.Owin.Diagnostics;

[assembly: OwinStartup(typeof(OwinStartup))]

namespace mobSocial.WebApi
{
    public class OwinStartup
    {
        public void Configuration(IAppBuilder app)
        {
            //new configuration for owin
            var config = new HttpConfiguration();
            config.IncludeErrorDetailPolicy = IncludeErrorDetailPolicy.Always;
            var enableCors = WebConfigurationManager.AppSettings["enableCors"] != null &&
                             WebConfigurationManager.AppSettings["enableCors"].ToLower() == "true";
            if (enableCors)
            {
                var origins = WebConfigurationManager.AppSettings["corsAllowedOrigins"] ?? "*";
                var cors = new EnableCorsAttribute(origins, "*", "GET,POST,PUT,DELETE");
                config.EnableCors(cors);
            };

            var enableSwagger = WebConfigurationManager.AppSettings["enableSwagger"] != null &&
                             WebConfigurationManager.AppSettings["enableSwagger"].ToLower() == "true";
            if (enableSwagger)
                SwaggerConfig.Register(config);

            //route registrations & other configurations
            WebApiConfig.Register(config);

            //run owin startup configurations from plugins
            OwinStartupManager.RunAllOwinConfigurations(app);

            app.MapWhen(x => x.Request.IsApiEndPointRequest() , builder =>
            {

                builder.UseInstallationVerifier();

               //builder.UseDryIocOwinMiddleware(mobSocialEngine.ActiveEngine.IocContainer);

                builder.UsePictureSizeRegistrar();

#if DEBUG
                builder.UseErrorPage(new ErrorPageOptions());
#endif
                builder.TrackApiUsage();
                //webapi, last one always 
                builder.UseWebApi(config);
            });

            app.MapWhen(x => x.Request.IsSignalRRequest(), builder =>
            {
                var userIdProvider = new SignalRUserIdProvider();
                GlobalHost.DependencyResolver.Register(typeof(IUserIdProvider), () => userIdProvider);
                var hubConfiguration = new HubConfiguration {EnableDetailedErrors = true};
                builder.MapSignalR(hubConfiguration);
            });
        }
    }
}