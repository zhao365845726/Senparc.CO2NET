using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using Senparc.CO2NET.AspNet;
using Senparc.CO2NET.Cache;
using Senparc.CO2NET.Cache.Memcached;
using Senparc.CO2NET.Helpers;
using Senparc.CO2NET.RegisterServices;
using Senparc.CO2NET.Sample.net6.Services;
using Senparc.CO2NET.WebApi;
using Senparc.CO2NET.WebApi.WebApiEngines;
using Swashbuckle.AspNetCore.Annotations;
using Swashbuckle.AspNetCore.SwaggerGen;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Senparc.CO2NET.Sample
{
    public class Startup
    {
        public Startup(IConfiguration configuration, IWebHostEnvironment env)
        {
            Configuration = configuration;
            WebHostEnvironment = env;
        }

        public IConfiguration Configuration { get; }
        public IWebHostEnvironment WebHostEnvironment { get; set; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddControllersWithViews();

            services.AddMemoryCache();//使用本地缓需要添加
            services.Add(ServiceDescriptor.Singleton(typeof(ILogger<>), typeof(Logger<>)));//使用 Memcached 或 Logger 需要添加
            var builder = services.AddMvcCore();
            //Senparc.CO2NET 全局注册（必须）
            services.AddSenparcGlobalServices(Configuration);

            #region WebApiEngine

            //忽略测试，注释掉以下代码后，可看到微信公众号SDK接口及注释信息
            WebApi.Register.OmitCategoryList.Add(NeuChar.PlatformType.WeChat_OfficialAccount.ToString());

            //额外增加测试
            WebApi.Register.AdditionalClasses.Add(typeof(AdditionalType), "Additional");
            WebApi.Register.AdditionalMethods.Add(typeof(AdditionalMethod).GetMethod("TestApi"), "Additional");
            WebApi.Register.AdditionalMethods.Add(typeof(EncryptHelper).GetMethod("GetMD5", new[] { typeof(string), typeof(string) }), "Additional");

            var docXmlPath = Path.Combine(WebHostEnvironment.ContentRootPath, "App_Data", "ApiDocXml");
            services.AddAndInitDynamicApi(builder, options =>
            {
                options.DocXmlPath = docXmlPath;
                options.DefaultRequestMethod = ApiRequestMethod.Get;
                options.BaseApiControllerType = null;
                options.CopyCustomAttributes = true;
                options.TaskCount = Environment.ProcessorCount * 4;
                options.ShowDetailApiLog = true;
                options.AdditionalAttributeFunc = null;
                options.ForbiddenExternalAccess = true;
            });

            #endregion

            #region 独立测试
            services.AddScoped(typeof(ApiBindTestService));
            services.AddScoped(typeof(EntityApiBindTestService));
            var apiBindTestService = new ApiBindTestService();
            apiBindTestService.DynamicBuild(services, builder);
            #endregion

            #region Swagger

            //.NET Core 3.0 for Swagger https://www.thecodebuzz.com/swagger-api-documentation-in-net-core-3-0/

            //添加Swagger
            services.AddSwaggerGen(c =>
            {
                //为每个程序集创建文档
                foreach (var apiAssembly in WebApiEngine.ApiAssemblyCollection)
                {
                    var version = WebApiEngine.ApiAssemblyVersions[apiAssembly.Key]; //neucharApiDocAssembly.Value.ImageRuntimeVersion;
                    var docName = WebApiEngine.GetDocName(apiAssembly.Key);
                    c.SwaggerDoc(docName, new OpenApiInfo
                    {
                        Title = $"CO2NET Dynamic WebApi Engine : {apiAssembly.Key}",
                        //Version = $"v{version}",//"v16.5.4"
                        Description = $"Senparc CO2NET WebApi 动态引擎（{apiAssembly.Key} - v{version}）",
                        //License = new OpenApiLicense()
                        //{
                        //    Name = "Apache License Version 2.0",
                        //    Url = new Uri("https://github.com/JeffreySu/WeiXinMPSDK")
                        //},

                        Contact = new OpenApiContact()
                        {
                            Email = "zsu@senparc.com",
                            Name = "Senparc Team",
                            Url = new Uri("https://www.senparc.com")
                        },
                        //TermsOfService = new Uri("https://github.com/JeffreySu/WeiXinMPSDK")
                    });

                    //c.DocumentFilter<TagDescriptionsDocumentFilter>();
                    var docXmlFile = Path.Combine(WebApiEngine.GetDynamicFilePath(docXmlPath), $"{WebApiEngine.ApiAssemblyNames[apiAssembly.Key]}.xml");
                    if (File.Exists(docXmlFile))
                    {
                        c.IncludeXmlComments(docXmlFile);
                    }
                }

                //分组显示  https://www.cnblogs.com/toiv/archive/2018/07/28/9379249.html
                c.DocInclusionPredicate((docName, apiDesc) =>
                {
                    if (!apiDesc.TryGetMethodInfo(out MethodInfo methodInfo))
                    {
                        return false;
                    }

                    //获取方法上的特性
                    var catalogNames = methodInfo.GetCustomAttributes(true)
                                              .OfType<SwaggerOperationAttribute>()
                                              .Select(z => z.Tags[0].Split(':')[0]);

                    if (catalogNames?.Count() == 0)
                    {
                        //获取类上的特性
                        catalogNames = methodInfo.DeclaringType.GetCustomAttributes(true)
                        .OfType<SwaggerOperationAttribute>()
                          .Select(z => z.Tags[0].Split(':')[0]);
                    }

                    if (catalogNames?.Count() == 0)
                    {
                        return false;//不符合要求的都不显示
                    }


                    //docName: $"{neucharApiDocAssembly.Key}-v1"
                    return catalogNames.Any(z => docName.StartsWith(z));
                });

                c.OrderActionsBy(z => z.RelativePath);
                //c.DescribeAllEnumsAsStrings();//枚举显示字符串
                c.EnableAnnotations();
                c.DocumentFilter<RemoveVerbsFilter>();
                c.CustomSchemaIds(x => x.FullName);//规避错误：InvalidOperationException: Can't use schemaId "$JsApiTicketResult" for type "$Senparc.Weixin.Open.Entities.JsApiTicketResult". The same schemaId was already used for type "$Senparc.Weixin.MP.Entities.JsApiTicketResult"

                /* 需要登陆，暂不考虑    —— Jeffrey Su 2021.06.17
                var oAuthDocName = "oauth2";// WeixinApiService.GetDocName(PlatformType.WeChat_OfficialAccount);

                //添加授权
                var authorizationUrl = NeuChar.App.AppStore.Config.IsDebug
                                               //以下是 appPurachase 的 Id，实际应该是 appId
                                               //? "http://localhost:12222/App/LoginOAuth/Authorize/1002/"
                                               //: "https://www.neuchar.com/App/LoginOAuth/Authorize/4664/";
                                               //以下是正确的 appId
                                               ? "http://localhost:12222/App/LoginOAuth/Authorize?appId=xxx"
                                               : "https://www.neuchar.com/App/LoginOAuth/Authorize?appId=3035";

                c.AddSecurityDefinition(oAuthDocName,//"Bearer" 
                    new OpenApiSecurityScheme
                    {
                        Description = "请输入带有Bearer开头的Token",
                        Name = oAuthDocName,// "Authorization",
                        In = ParameterLocation.Header,
                        Type = SecuritySchemeType.OAuth2,
                        //OpenIdConnectUrl = new Uri("https://www.neuchar.com/"),
                        Flows = new OpenApiOAuthFlows()
                        {
                            Implicit = new OpenApiOAuthFlow()
                            {
                                AuthorizationUrl = new Uri(authorizationUrl),
                                Scopes = new Dictionary<string, string> { { "swagger_api", "Demo API - full access" } }
                            }
                        }
                    });

                //认证方式，此方式为全局添加
                c.AddSecurityRequirement(new OpenApiSecurityRequirement()
                {
                    { new OpenApiSecurityScheme(){ Name =oAuthDocName//"Bearer"
                    }, new List<string>() }
                    //{ "Bearer", Enumerable.Empty<string>() }
                });

                //c.OperationFilter<AuthResponsesOperationFilter>();//AuthorizeAttribute过滤

                */

            });
            #endregion

        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, IOptions<SenparcSetting> senparcSetting)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Home/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }
            //app.UseHttpsRedirection();
            app.UseStaticFiles();
            app.UseRouting();

            app.UseAuthorization();

            // 启动 CO2NET 全局注册，必须！
            app.UseSenparcGlobal(env, senparcSetting.Value, register =>
                {
                    #region CO2NET 全局配置

                    #region 全局缓存配置（按需）

                    //当同一个分布式缓存同时服务于多个网站（应用程序池）时，可以使用命名空间将其隔离（非必须）
                    register.ChangeDefaultCacheNamespace("CO2NETCache.net6.0");

                    #region 配置和使用 Redis

                    //配置全局使用Redis缓存（按需，独立）
                    var redisConfigurationStr = senparcSetting.Value.Cache_Redis_Configuration;
                    var useRedis = !string.IsNullOrEmpty(redisConfigurationStr) && redisConfigurationStr != "Redis配置";
                    if (useRedis)//这里为了方便不同环境的开发者进行配置，做成了判断的方式，实际开发环境一般是确定的，这里的if条件可以忽略
                    {
                        /* 说明：
                         * 1、Redis 的连接字符串信息会从 Config.SenparcSetting.Cache_Redis_Configuration 自动获取并注册，如不需要修改，下方方法可以忽略
                        /* 2、如需手动修改，可以通过下方 SetConfigurationOption 方法手动设置 Redis 链接信息（仅修改配置，不立即启用）
                         */
                        Senparc.CO2NET.Cache.CsRedis.Register.SetConfigurationOption(redisConfigurationStr);

                        //以下会立即将全局缓存设置为 Redis
                        Senparc.CO2NET.Cache.CsRedis.Register.UseKeyValueRedisNow();//键值对缓存策略（推荐）
                        //Senparc.CO2NET.Cache.Redis.Register.UseHashRedisNow();//HashSet储存格式的缓存策略

                        //也可以通过以下方式自定义当前需要启用的缓存策略
                        //CacheStrategyFactory.RegisterObjectCacheStrategy(() => RedisObjectCacheStrategy.Instance);//键值对
                        //CacheStrategyFactory.RegisterObjectCacheStrategy(() => RedisHashSetObjectCacheStrategy.Instance);//HashSet
                    }
                    //如果这里不进行Redis缓存启用，则目前还是默认使用内存缓存 

                    #endregion

                    #region 配置和使用 Memcached

                    //配置Memcached缓存（按需，独立）
                    var memcachedConfigurationStr = senparcSetting.Value.Cache_Memcached_Configuration;
                    var useMemcached = !string.IsNullOrEmpty(memcachedConfigurationStr) && memcachedConfigurationStr != "Memcached配置";

                    if (useMemcached) //这里为了方便不同环境的开发者进行配置，做成了判断的方式，实际开发环境一般是确定的，这里的if条件可以忽略
                    {
                        app.UseEnyimMemcached();

                        /* 说明：
                        * 1、Memcached 的连接字符串信息会从 Config.SenparcSetting.Cache_Memcached_Configuration 自动获取并注册，如不需要修改，下方方法可以忽略
                       /* 2、如需手动修改，可以通过下方 SetConfigurationOption 方法手动设置 Memcached 链接信息（仅修改配置，不立即启用）
                        */
                        Senparc.CO2NET.Cache.Memcached.Register.SetConfigurationOption(redisConfigurationStr);

                        //以下会立即将全局缓存设置为 Memcached
                        Senparc.CO2NET.Cache.Memcached.Register.UseMemcachedNow();

                        //也可以通过以下方式自定义当前需要启用的缓存策略
                        CacheStrategyFactory.RegisterObjectCacheStrategy(() => MemcachedObjectCacheStrategy.Instance);
                    }

                    #endregion

                    #endregion

                    #region 注册日志（按需，建议）

                    register.RegisterTraceLog(ConfigTraceLog);//配置TraceLog

                    #endregion

                    #endregion
                },

            #region 扫描自定义扩展缓存

                //自动扫描自定义扩展缓存（二选一）
                autoScanExtensionCacheStrategies: true //默认为 true，可以不传入
                                                       //指定自定义扩展缓存（二选一）
                                                       //autoScanExtensionCacheStrategies: false, extensionCacheStrategiesFunc: () => GetExCacheStrategies(senparcSetting.Value)

            #endregion
            );

            app.UseSwagger();
            app.UseSwaggerUI(c =>
            {
                //c.DocumentTitle = "Senparc Weixin SDK Demo API";
                c.InjectJavascript("/lib/jquery/dist/jquery.min.js");
                c.InjectJavascript("/js/swagger.js");
                //c.InjectJavascript("/js/tongji.js");
                c.DocExpansion(Swashbuckle.AspNetCore.SwaggerUI.DocExpansion.None);

                foreach (var co2netApiDocAssembly in WebApiEngine.ApiAssemblyCollection)
                {
                    //TODO:真实的动态版本号
                    var version = WebApiEngine.ApiAssemblyVersions[co2netApiDocAssembly.Key]; //neucharApiDocAssembly.Value.ImageRuntimeVersion;
                    var docName = WebApiEngine.GetDocName(co2netApiDocAssembly.Key);

                    //Console.WriteLine($"\tAdd {docName}");

                    c.SwaggerEndpoint($"/swagger/{docName}/swagger.json", $"{co2netApiDocAssembly.Key}");
                }

                //OAuth     https://www.cnblogs.com/miskis/p/10083985.html
                c.OAuthClientId("e65ea785b96b442a919965ccf857aba3");//客服端名称
                c.OAuthAppName("微信 API Swagger 文档 "); // 描述
            });


            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllerRoute(
                    name: "default",
                    pattern: "{controller=Home}/{action=Index}/{id?}");
            });

        }

        /// <summary>
        /// 配置全局跟踪日志
        /// </summary>
        private void ConfigTraceLog()
        {
            //这里设为Debug状态时，/App_Data/SenparcTraceLog/目录下会生成日志文件记录所有的API请求日志，正式发布版本建议关闭

            //如果全局的IsDebug（Senparc.CO2NET.Config.IsDebug）为false，此处可以单独设置true，否则自动为true
            CO2NET.Trace.SenparcTrace.SendCustomLog("系统日志", "系统启动");//只在Senparc.CO2NET.Config.IsDebug = true的情况下生效

            //全局自定义日志记录回调
            CO2NET.Trace.SenparcTrace.OnLogFunc = () =>
            {
                //加入每次触发Log后需要执行的代码
            };

            CO2NET.Trace.SenparcTrace.OnBaseExceptionFunc = ex =>
            {
                //加入每次触发BaseException后需要执行的代码
            };
        }

        /// <summary>
        /// 获取扩展缓存策略
        /// </summary>
        /// <returns></returns>
        private IList<IDomainExtensionCacheStrategy> GetExCacheStrategies(SenparcSetting senparcSetting)
        {
            var exContainerCacheStrategies = new List<IDomainExtensionCacheStrategy>();
            senparcSetting = senparcSetting ?? new SenparcSetting();

            //注意：以下两个 if 判断仅作为演示，方便大家添加自定义的扩展缓存策略，

            #region 演示扩展缓存注册方法

            /*

            //判断Redis是否可用
            var redisConfiguration = senparcSetting.Cache_Redis_Configuration;
            if ((!string.IsNullOrEmpty(redisConfiguration) && redisConfiguration != "Redis配置"))
            {
                exContainerCacheStrategies.Add(RedisContainerCacheStrategy.Instance);//自定义的扩展缓存
            }

            //判断Memcached是否可用
            var memcachedConfiguration = senparcSetting.Cache_Memcached_Configuration;
            if ((!string.IsNullOrEmpty(memcachedConfiguration) && memcachedConfiguration != "Memcached配置"))
            {
                exContainerCacheStrategies.Add(MemcachedContainerCacheStrategy.Instance);//TODO:如果没有进行配置会产生异常
            }
            */

            #endregion

            //扩展自定义的缓存策略

            return exContainerCacheStrategies;
        }


        class RemoveVerbsFilter : IDocumentFilter
        {
            //public void Apply(SwaggerDocument swaggerDoc, SchemaRegistry schemaRegistry, IApiExplorer apiExplorer)
            //{
            //    foreach (PathItem path in swaggerDoc.paths.Values)
            //    {
            //        path.delete = null;
            //        //path.get = null; // leaving GET in
            //        path.head = null;
            //        path.options = null;
            //        path.patch = null;
            //        path.post = null;
            //        path.put = null;
            //    }
            //}

            public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
            {
                //每次切换定义，都需要经过比较长的时间才到达这里
                return;
                string platformType;
                var title = swaggerDoc.Info.Title;

                //if (title.Contains(PlatformType.WeChat_OfficialAccount.ToString()))
                //{
                //    platformType = PlatformType.WeChat_OfficialAccount.ToString();
                //}
                //else if (title.Contains(PlatformType.WeChat_Work.ToString()))
                //{
                //    platformType = PlatformType.WeChat_Work.ToString();
                //}
                //else if (title.Contains(PlatformType.WeChat_Open.ToString()))
                //{
                //    platformType = PlatformType.WeChat_Open.ToString();
                //}
                //else if (title.Contains(PlatformType.WeChat_MiniProgram.ToString()))
                //{
                //    platformType = PlatformType.WeChat_MiniProgram.ToString();
                //}
                ////else if (title.Contains(PlatformType.General.ToString()))
                ////{
                ////    platformType = PlatformType.General.ToString();
                ////}
                //else
                //{
                //    throw new NotImplementedException($"未提供的 PlatformType 类型，Title：{title}");
                //}

                //var pathList = swaggerDoc.Paths.Keys.ToList();

                //foreach (var path in pathList)
                //{
                //    if (!path.Contains(platformType))
                //    {
                //        //移除非当前模块的API对象
                //        swaggerDoc.Paths.Remove(path);
                //    }
                //}

                //SwaggerOperationAttribute
                //移除Schema对象
                //var toRemoveSchema = context.SchemaRepository.Schemas.Where(z => !z.Key.Contains(platformType)).ToList();//结果为全部删除，仅测试
                //foreach (var schema in toRemoveSchema)
                //{
                //    context.SchemaRepository.Schemas.Remove(schema.Key);
                //}
            }
        }

        //public class AuthResponsesOperationFilter : IOperationFilter
        //{
        //    public void Apply(OpenApiOperation operation, OperationFilterContext context)
        //    {
        //        //获取是否添加登录特性
        //        var authAttributes = context.MethodInfo.DeclaringType.GetCustomAttributes(true)
        //         .Union(context.MethodInfo.GetCustomAttributes(true))
        //         .OfType<AuthorizeAttribute>().Any();

        //        if (authAttributes)
        //        {
        //            operation.Responses.Add("401", new OpenApiResponse { Description = "暂无访问权限" });
        //            operation.Responses.Add("403", new OpenApiResponse { Description = "禁止访问" });
        //            operation.Security = new List<OpenApiSecurityRequirement>
        //            {
        //                new OpenApiSecurityRequirement { { new OpenApiSecurityScheme() {  Name= "oauth2" }, new[] { "swagger_api" } }}
        //            };
        //        }
        //    }
        //}

        // public class TagDescriptionsDocumentFilter : IDocumentFilter
        // {
        //     public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
        //     {
        //         swaggerDoc.Tags = new List<OpenApiTag> {
        //     new OpenApiTag { Name = "Products", Description = "Browse/manage the product cata,og" },
        //     new OpenApiTag { Name = "Orders", Description = "Submit orders" },
        //};
        //     }
        // }

    }
}
