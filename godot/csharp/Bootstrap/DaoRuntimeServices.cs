using Nikami.Aurora.GodotRuntime.Application.Abstractions;
using Nikami.Aurora.GodotRuntime.Application.Characters;
using Nikami.Aurora.GodotRuntime.Domain.Abilities;
using Nikami.Aurora.GodotRuntime.Domain.Combat;
using Nikami.Aurora.GodotRuntime.Domain.Inventory;
using Nikami.Aurora.GodotRuntime.Domain.Party;
using Nikami.Aurora.GodotRuntime.Domain.Quests;
using Nikami.Aurora.GodotRuntime.Domain.Story;
using Nikami.Aurora.GodotRuntime.Infrastructure.Catalogs;
using Nikami.Aurora.GodotRuntime.Infrastructure.Configuration;
using Nikami.Aurora.GodotRuntime.Infrastructure.Persistence;
using Nikami.Aurora.GodotRuntime.Infrastructure.Time;
using Nikami.Aurora.GodotRuntime.Infrastructure.World;
using Nikami.Aurora.GodotRuntime.Presentation.Cinematics;

namespace Nikami.Aurora.GodotRuntime.Bootstrap;

/// <summary>
/// Shared composition root owned by the Aurora DAO adapter. Origin identities
/// and behavior stay in the DAO profile while every route uses this same
/// runtime object graph.
/// </summary>
internal sealed class DaoRuntimeServices : IDisposable
{
    private readonly Dictionary<Type, object> services = [];

    public DaoRuntimeServices()
    {
        var clock = new GodotClock();
        var environment = new GodotRuntimeEnvironment();
        var store = new JsonFileStore(environment);
        var sessionRepository = new PlayerSessionRepository(store, environment);
        var worldProfileProvider = new WorldProfileProvider(store, environment);
        var arrivalResolver = new GodotWorldArrivalResolver(store);
        var characterProfileProvider = new CharacterProfileProvider(store, environment);
        var characterModelResolver = new CachedDaoCharacterModelResolver();
        var characterCinematicModelResolver = new CachedDaoCharacterCinematicModelResolver();
        var cinematicActorModelResolver = new CachedDaoCinematicActorModelResolver();
        var locomotionProvider = new CachedDaoLocomotionAnimationProvider();
        var characterMaterials = new DaoCharacterMaterialPostprocessor();
        var modelCache = new GodotModelCache(characterMaterials);
        var scheduler = new GodotWorldLoadScheduler();
        var navigation = new DaoArlNavigationGridSource();
        var blockers = new AuthoredWorldBlockerBuilder();
        var lighting = new DaoAuthoredLightingResolver();
        var terrainMaterials = new DaoTerrainMaterialFactory(store);
        var waterMaterials = new DaoWaterMaterialFactory(store);
        var batches = new StaticWorldBatchBuilder(scheduler, characterMaterials);
        var story = new StoryState();
        var areaStoryContracts = new DaoAreaStoryContractProvider(store, environment);
        var scriptBytecode = new DaoScriptBytecodeProvider(environment);
        var worldLoader = new GodotWorldContentLoader(store, modelCache, batches,
            terrainMaterials, waterMaterials, navigation, blockers, lighting,
            areaStoryContracts, story, scheduler);
        var abilityCatalog = new GdaAbilityCatalog(store);
        var areaPresentation = new DaoAreaPresentationProvider(store);
        var characterLoadouts = new GdaCharacterAbilityLoadoutProvider(store, environment);
        var experienceTables = new GdaExperienceTableProvider(store, environment);
        var abilities = new AbilityState(clock);
        var abilityInitializer = new CharacterAbilityInitializer(
            characterLoadouts, abilityCatalog, abilities);
        var storyInitializer = new CharacterStoryInitializer(story);

        Add<IClock>(clock);
        Add<IRuntimeEnvironment>(environment);
        Add<IJsonStore>(store);
        Add<IPlayerSessionRepository>(sessionRepository);
        Add<IWorldProfileProvider>(worldProfileProvider);
        Add<IWorldArrivalResolver>(arrivalResolver);
        Add<ICharacterProfileProvider>(characterProfileProvider);
        Add<ICharacterModelResolver>(characterModelResolver);
        Add<ICharacterCinematicModelResolver>(characterCinematicModelResolver);
        Add<ICinematicActorModelResolver>(cinematicActorModelResolver);
        Add<ILocomotionAnimationProvider>(locomotionProvider);
        Add<IGodotModelPostprocessor>(characterMaterials);
        Add<ICharacterLightingBinder>(characterMaterials);
        Add<IGodotModelCache>(modelCache);
        Add<IWorldLoadScheduler>(scheduler);
        Add<IAuthoredNavigationGridSource>(navigation);
        Add<IAuthoredWorldBlockerBuilder>(blockers);
        Add<IAuthoredLightingResolver>(lighting);
        Add<IDaoTerrainMaterialFactory>(terrainMaterials);
        Add<IDaoWaterMaterialFactory>(waterMaterials);
        Add<IStaticWorldBatchBuilder>(batches);
        Add<IWorldContentLoader>(worldLoader);
        Add<IAbilityCatalog>(abilityCatalog);
        Add<IAreaPresentationProvider>(areaPresentation);
        Add<ICharacterAbilityLoadoutProvider>(characterLoadouts);
        Add<GdaExperienceTableProvider>(experienceTables);
        Add(areaStoryContracts);
        Add(scriptBytecode);
        Add(abilityInitializer);
        Add(storyInitializer);
        Add(abilities);
        Add(story);
        Add(new CombatState());
        Add(new PartyState());
        Add(new InventoryState());
        Add(new QuestJournal());
        Add(new FaceFxRuntime());
    }

    public T GetRequired<T>() where T : class =>
        services.TryGetValue(typeof(T), out var service)
            ? (T)service
            : throw new InvalidOperationException(
                $"DAO runtime service is not registered: {typeof(T).FullName}");

    public void Dispose()
    {
        foreach (var disposable in services.Values
                     .Distinct(ReferenceEqualityComparer.Instance)
                     .OfType<IDisposable>())
            disposable.Dispose();
        services.Clear();
    }

    private void Add<T>(T service) where T : class => services.Add(typeof(T), service);
}
