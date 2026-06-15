using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Autofac;
using Mapster;
using MapsterMapper;
using Miningcore.Configuration;
using Miningcore.Tests.Util;
using Miningcore.Time;

namespace Miningcore.Tests;

public static class ModuleInitializer
{
    private static readonly object initLock = new object();

    private static bool isInitialized = false;
    private static IContainer container;
    private static Dictionary<string, CoinTemplate> coinTemplates;

    public static IContainer Container => container;
    public static Dictionary<string, CoinTemplate> CoinTemplates => coinTemplates;

    public static void Initialize()
    {
        lock(initLock)
        {
            if(isInitialized)
                return;

            var builder = new ContainerBuilder();

            builder.RegisterAssemblyModules(typeof(AutofacModule).GetTypeInfo().Assembly);

            // Mapster
            var mapsterConfig = new TypeAdapterConfig();
            mapsterConfig.Apply(new Miningcore.MapsterConfig());

            builder.RegisterInstance(mapsterConfig);
            builder.Register(ctx => new Mapper(ctx.Resolve<TypeAdapterConfig>()))
                .As<IMapper>()
                .SingleInstance();

            builder.RegisterType<MockMasterClock>().AsImplementedInterfaces();

            container = builder.Build();

            isInitialized = true;

            var basePath = Path.GetDirectoryName(typeof(Program).Assembly.Location)
                ?? throw new InvalidOperationException("Cannot determine assembly directory; test fixture cannot locate coins.json.");
            var defaultDefinitions = Path.Combine(basePath, "coins.json");

            var coinDefs = new[]
            {
                defaultDefinitions
            };

            coinTemplates = CoinTemplateLoader.Load(container, coinDefs);
        }
    }
}
