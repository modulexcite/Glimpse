using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Glimpse.Core.Extensibility;
using Glimpse.Core.Extensions;
using Glimpse.Core.Message; 
using Glimpse.Core.ResourceResult;
#if NET35
using Glimpse.Core.Backport;
#endif

namespace Glimpse.Core.Framework
{
    /// <summary>
    /// The heart and soul of Glimpse. The runtime coordinate all input from a <see cref="IRequestResponseAdapter" />, persists collected runtime information and writes responses out to the <see cref="IRequestResponseAdapter" />.
    /// </summary>
    public class GlimpseRuntime : IGlimpseRuntime
    {
        private static readonly MethodInfo MethodInfoBeginRequest = typeof(GlimpseRuntime).GetMethod("BeginRequest", BindingFlags.Public | BindingFlags.Instance);
        private static readonly MethodInfo MethodInfoEndRequest = typeof(GlimpseRuntime).GetMethod("EndRequest", BindingFlags.Public | BindingFlags.Instance);
        private static readonly object LockObj = new object();
        private static GlimpseRuntime instance;

        /// <summary>
        /// Initializes static members of the <see cref="GlimpseRuntime" /> class.
        /// </summary>
        /// <exception cref="System.NullReferenceException">BeginRequest method not found</exception>
        static GlimpseRuntime()
        {
            // Version is in major.minor.build format to support http://semver.org/ 
            IsInitialized = false;

            if (MethodInfoBeginRequest == null)
            {
                throw new NullReferenceException("BeginRequest method not found");
            }

            if (MethodInfoEndRequest == null)
            {
                throw new NullReferenceException("EndRequest method not found");
            }
        }

        internal static void Reset()
        {
            instance = null; // HACK?
        }

        /// <summary>
        /// Gets the singleton instance of the <see cref="GlimpseRuntime"/> type once it has been initialized
        /// </summary>
        public static GlimpseRuntime Instance
        {
            get
            {
                if (instance == null)
                {
                    throw new GlimpseNotInitializedException();
                }

                return instance;
            }

            private set { instance = value; }
        }

        /// <summary>
        /// Gets a value indicating whether this instance has been initialized.
        /// </summary>
        /// <value>
        /// <c>true</c> if this instance is initialized; otherwise, <c>false</c>.
        /// </value>
        public static bool IsInitialized { get; private set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="GlimpseRuntime" /> class.
        /// </summary>
        /// <param name="configuration">The configuration.</param>
        /// <exception cref="System.ArgumentNullException">Throws an exception if <paramref name="configuration"/> is <c>null</c>.</exception>
        public static void Initialize(IConfiguration configuration)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException("configuration");
            }

            if (configuration.DefaultRuntimePolicy == RuntimePolicy.Off)
            {
                return;
            }

            if (!IsInitialized) // Double checked lock to ensure thread safety. http://en.wikipedia.org/wiki/Double_checked_locking_pattern
            {
                lock (LockObj)
                {
                    if (!IsInitialized)
                    {
                        Instance = new GlimpseRuntime(configuration);
                    }
                }
            }
        }

        // TODO: V2Merge This should be private but is internal to not break unit tests
        internal GlimpseRuntime(IConfiguration configuration) 
        {
            InitializeConfig(configuration);
            Initialize();

            IsInitialized = true;
        }

        /// <summary>
        /// Gets or sets the configuration.
        /// </summary>
        /// <value>
        /// The configuration.
        /// </value>
        public IReadonlyConfiguration Configuration { get; set; }

        /// <summary>
        /// Returns the <see cref="IGlimpseRequestContext"/> corresponding to the current request.
        /// </summary>
        public IGlimpseRequestContext CurrentRequestContext
        {
            get { return ActiveGlimpseRequestContexts.Current; }
        }

        private ActiveGlimpseRequestContexts ActiveGlimpseRequestContexts { get; set; }

        private RuntimePolicyDeterminator RuntimePolicyDeterminator { get; set; }

        private MetadataProvider MetadataProvider { get; set; }
         
        private DisplayProvider DisplayProvider { get; set; }

        private TabProvider TabProvider { get; set; }

        private InspectorProvider InspectorProvider { get; set; } 

        /// <summary>
        /// Begins Glimpse's processing of a Http request.
        /// </summary>
        /// <exception cref="Glimpse.Core.Framework.GlimpseException">Throws an exception if <see cref="GlimpseRuntime"/> is not yet initialized.</exception>
        public GlimpseRequestContextHandle BeginRequest(IRequestResponseAdapter requestResponseAdapter)
        {
            var glimpseRequestContext = new GlimpseRequestContext(Guid.NewGuid(), requestResponseAdapter, Configuration.DefaultRuntimePolicy, Configuration.ResourceEndpoint, Configuration.EndpointBaseUri);

            var runtimePolicy = RuntimePolicyDeterminator.DetermineRuntimePolicy(RuntimeEvent.BeginRequest, glimpseRequestContext.CurrentRuntimePolicy, glimpseRequestContext.RequestResponseAdapter);
            if (runtimePolicy == RuntimePolicy.Off)
            {
                return UnavailableGlimpseRequestContextHandle.Instance;
            }

            glimpseRequestContext.CurrentRuntimePolicy = runtimePolicy;

            var glimpseRequestContextHandle = ActiveGlimpseRequestContexts.Add(glimpseRequestContext);

            // When we are dealing with a resource request, there is no need to further 
            // continue setting up the request.
            if (glimpseRequestContextHandle.RequestHandlingMode == RequestHandlingMode.ResourceRequest)
            {
                return glimpseRequestContextHandle;
            }

            try
            {
                glimpseRequestContext.StartTiming();

                TabProvider.Execute(glimpseRequestContext, RuntimeEvent.BeginRequest);

                GlimpseTimeline.CaptureMoment("Start Request", TimelineCategory.Request, new RuntimeMessage().AsSourceMessage(typeof(GlimpseRuntime), MethodInfoBeginRequest));

                return glimpseRequestContextHandle;
            }
            catch
            {
                // We need to deactivate here because the handle won't be returned to the caller
                glimpseRequestContextHandle.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Ends Glimpse's processing of the request referenced by the given <paramref name="glimpseRequestContextHandle"/>"/>
        /// </summary>
        /// <param name="glimpseRequestContextHandle">The Glimpse handle of the corresponding request</param>
        /// <exception cref="Glimpse.Core.Framework.GlimpseException">Throws an exception if <c>BeginRequest</c> has not yet been called for the given request.</exception>
        public void EndRequest(GlimpseRequestContextHandle glimpseRequestContextHandle) 
        {
            if (glimpseRequestContextHandle == null)
            {
                throw new ArgumentNullException("glimpseRequestContextHandle");
            }

            try
            {
                IGlimpseRequestContext glimpseRequestContext;
                if (!ContinueProcessingRequest(glimpseRequestContextHandle, RuntimeEvent.EndRequest, RequestHandlingMode.RegularRequest, out glimpseRequestContext))
                {
                    return;
                }

                GlimpseTimeline.CaptureMoment("End Request", TimelineCategory.Request, new RuntimeMessage().AsSourceMessage(typeof(GlimpseRuntime), MethodInfoBeginRequest));

                TabProvider.Execute(glimpseRequestContext, RuntimeEvent.EndRequest);
                DisplayProvider.Execute(glimpseRequestContext);

                var timingDuration = glimpseRequestContext.StopTiming();
                var requestResponseAdapter = glimpseRequestContext.RequestResponseAdapter;
                var requestMetadata = requestResponseAdapter.RequestMetadata;
                var runtimePolicy = glimpseRequestContext.CurrentRuntimePolicy;

                if (runtimePolicy.HasFlag(RuntimePolicy.PersistResults))
                {
                    var persistenceStore = Configuration.PersistenceStore;
                    var metadata = new GlimpseRequest(glimpseRequestContext.GlimpseRequestId, requestMetadata, TabProvider.GetResultsStore(glimpseRequestContext), DisplayProvider.GetResultsStore(glimpseRequestContext), timingDuration, MetadataProvider.GetRequestMetadata(glimpseRequestContext));

                    try
                    {
                        persistenceStore.Save(metadata);
                    }
                    catch (Exception exception)
                    {
                        Configuration.Logger.Error(Resources.GlimpseRuntimeEndRequesPersistError, exception, persistenceStore.GetType());
                    }
                }

                if (runtimePolicy.HasFlag(RuntimePolicy.ModifyResponseHeaders))
                {
                    requestResponseAdapter.SetHttpResponseHeader(Constants.HttpResponseHeader, glimpseRequestContext.GlimpseRequestId.ToString());

                    if (requestMetadata.GetCookie(Constants.ClientIdCookieName) == null)
                    {
                        requestResponseAdapter.SetCookie(Constants.ClientIdCookieName, requestMetadata.ClientId);
                    }
                }

                if (runtimePolicy.HasFlag(RuntimePolicy.DisplayGlimpseClient))
                {
                    var html = GenerateScriptTags(glimpseRequestContext);

                    requestResponseAdapter.InjectHttpResponseBody(html);
                }
            }
            finally
            {
                glimpseRequestContextHandle.Dispose();
            }
        }

        /// <summary>
        /// Begins access to session data.
        /// </summary>
        public void BeginSessionAccess(GlimpseRequestContextHandle glimpseRequestContextHandle)
        {
            IGlimpseRequestContext glimpseRequestContext;
            if (ContinueProcessingRequest(glimpseRequestContextHandle, RuntimeEvent.BeginSessionAccess, RequestHandlingMode.RegularRequest, out glimpseRequestContext))
            {
#warning should we add a try catch around this? So that failures in Glimpse don't fail the normal flow?
                TabProvider.Execute(glimpseRequestContext, RuntimeEvent.BeginSessionAccess);
            }
        }

        /// <summary>
        /// Ends access to session data.
        /// </summary>
        public void EndSessionAccess(GlimpseRequestContextHandle glimpseRequestContextHandle)
        {
            IGlimpseRequestContext glimpseRequestContext;
            if (ContinueProcessingRequest(glimpseRequestContextHandle, RuntimeEvent.EndSessionAccess, RequestHandlingMode.RegularRequest, out glimpseRequestContext))
            {
#warning should we add a try catch around this? So that failures in Glimpse don't fail the normal flow?
                TabProvider.Execute(glimpseRequestContext, RuntimeEvent.EndSessionAccess);
            }
        }

#warning CGI: There is no need to keep both execute methods, just have one default to default resource when resourcename is null
        /// <summary>
        /// Executes the default resource.
        /// </summary>
        public void ExecuteDefaultResource(GlimpseRequestContextHandle glimpseRequestContextHandle)
        {
            ExecuteResource(glimpseRequestContextHandle, Configuration.DefaultResource.Name, ResourceParameters.None());
        }

        /// <summary>
        /// Executes the given resource.
        /// </summary>
        /// <param name="glimpseRequestContextHandle">The Glimpse handle of the corresponding request</param>
        /// <param name="resourceName">Name of the resource.</param>
        /// <param name="parameters">The parameters.</param>
        /// <exception cref="System.ArgumentNullException">Throws an exception if either parameter is <c>null</c>.</exception>
        public void ExecuteResource(GlimpseRequestContextHandle glimpseRequestContextHandle, string resourceName, ResourceParameters parameters)
        {
            if (glimpseRequestContextHandle == null)
            {
                throw new ArgumentNullException("glimpseRequestContextHandle");
            }

            if (string.IsNullOrEmpty(resourceName))
            {
                throw new ArgumentNullException("resourceName");
            }

            if (parameters == null)
            {
                throw new ArgumentNullException("parameters");
            }

            IGlimpseRequestContext glimpseRequestContext;
            if (!ContinueProcessingRequest(glimpseRequestContextHandle, RuntimeEvent.ExecuteResource, RequestHandlingMode.ResourceRequest, out glimpseRequestContext))
            {
                return;
            }

            var requestResponseAdapter = glimpseRequestContext.RequestResponseAdapter;

            // First we get the current policy as it has been processed so far
            var policy = glimpseRequestContext.CurrentRuntimePolicy;

            // It is possible that the policy now says Off, but if the requested resource is the 
            // default resource or one of it dependent resources, then we need to make sure there 
            // is a good reason for not executing that resource, since the default resource (or 
            // one of it dependencies) is the one we most likely need to set Glimpse On with in the 
            // first place.
            var defaultResourceDependsOnResources = Configuration.DefaultResource as IDependOnResources;
            if (resourceName.Equals(Configuration.DefaultResource.Name) || (defaultResourceDependsOnResources != null && defaultResourceDependsOnResources.DependsOn(resourceName)))
            {
                // To be clear we only do this for the default resource (or its dependencies), and 
                // we do this because it allows us to secure the default resource the same way as 
                // any other resource, but for this we only rely on runtime policies that handle 
                // ExecuteResource runtime events and we ignore ignore previously executed runtime 
                // policies (most likely during BeginRequest). Either way, the default runtime policy 
                // is still our starting point and when it says Off, it remains Off
                policy = RuntimePolicyDeterminator.DetermineRuntimePolicy(RuntimeEvent.ExecuteResource, Configuration.DefaultRuntimePolicy, requestResponseAdapter);
            }

            var result = (IResourceResult)null;
            var message = (string)null;
            var logger = Configuration.Logger;

            if (policy == RuntimePolicy.Off)
            {
                message = string.Format(Resources.ExecuteResourceInsufficientPolicy, resourceName);
                logger.Info(message);
                result = new StatusCodeResourceResult(403, message);
            }
            else
            {
                var resources = Configuration.Resources.Where(r => r.Name.Equals(resourceName, StringComparison.InvariantCultureIgnoreCase));
                switch (resources.Count())
                {
                    case 1: // 200 - OK
                        try
                        {
                            var resource = resources.First();
                            var resourceContext = new ResourceContext(parameters.GetParametersFor(resource), Configuration.PersistenceStore, logger);

                            var privilegedResource = resource as IPrivilegedResource;

                            if (privilegedResource != null)
                            {
                                result = privilegedResource.Execute(resourceContext, Configuration, requestResponseAdapter);
                            }
                            else
                            {
                                result = resource.Execute(resourceContext);
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.Error(Resources.GlimpseRuntimeExecuteResourceError, ex, resourceName);
                            result = new ExceptionResourceResult(ex);
                        }

                        break;
                    case 0: // 404 - File Not Found
                        message = string.Format(Resources.ExecuteResourceMissingError, resourceName);
                        logger.Warn(message);
                        result = new StatusCodeResourceResult(404, message);
                        break;
                    default: // 500 - Server Error
                        message = string.Format(Resources.ExecuteResourceDuplicateError, resourceName);
                        logger.Warn(message);
                        result = new StatusCodeResourceResult(500, message);
                        break;
                }
            }

            try
            {
                var context = new ResourceResultContext(logger, requestResponseAdapter, Configuration.Serializer, Configuration.HtmlEncoder);

                result.Execute(context);
            }
            catch (Exception exception)
            {
                logger.Fatal(Resources.GlimpseRuntimeExecuteResourceResultError, exception, result.GetType());
            }
        }

        /// <summary>
        /// Returns the corresponding <see cref="IGlimpseRequestContext"/> for the given <paramref name="glimpseRequestId"/>
        /// </summary>
        /// <param name="glimpseRequestId">The Glimpse request Id</param>
        /// <param name="glimpseRequestContext">The corresponding <see cref="IGlimpseRequestContext"/></param>
        /// <returns>Boolean indicating whether the corresponding <see cref="IGlimpseRequestContext"/> was found.</returns>
        public bool TryGetRequestContext(Guid glimpseRequestId, out IGlimpseRequestContext glimpseRequestContext)
        {
            return ActiveGlimpseRequestContexts.TryGet(glimpseRequestId, out glimpseRequestContext);
        }

        private bool ContinueProcessingRequest(GlimpseRequestContextHandle glimpseRequestContextHandle, RuntimeEvent runtimeEvent, RequestHandlingMode allowedRequestHandlingMode, out IGlimpseRequestContext glimpseRequestContext)
        {
            glimpseRequestContext = null;

            if (glimpseRequestContextHandle == null)
            {
                throw new ArgumentNullException("glimpseRequestContextHandle");
            }

            if (glimpseRequestContextHandle.RequestHandlingMode != allowedRequestHandlingMode)
            {
                return false;
            }

            if (!TryGetRequestContext(glimpseRequestContextHandle.GlimpseRequestId, out glimpseRequestContext))
            {
#warning or maybe only a log and return false instead of throwing an exception? It is an isue though!
                throw new GlimpseException("No corresponding GlimpseRequestContext found for GlimpseRequestId '" + glimpseRequestContextHandle.GlimpseRequestId + "'.");
            }

            glimpseRequestContext.CurrentRuntimePolicy = RuntimePolicyDeterminator.DetermineRuntimePolicy(runtimeEvent, glimpseRequestContext.CurrentRuntimePolicy, glimpseRequestContext.RequestResponseAdapter);

            return glimpseRequestContext.CurrentRuntimePolicy != RuntimePolicy.Off;

        }

        private void InitializeConfig(IConfiguration configuration)
        {
            // Run user customizations to configuration before storing and then override 
            // (some) changes made by the user to make sure .config file driven settings win
            var userUpdatedConfig = GlimpseConfiguration.Override(configuration);
            userUpdatedConfig.ApplyOverrides();

            Configuration = new ReadonlyConfigurationAdapter(userUpdatedConfig);
        }

        private void Initialize()
        { 
            ActiveGlimpseRequestContexts = new ActiveGlimpseRequestContexts(Configuration.CurrentGlimpseRequestIdTracker);

            RuntimePolicyDeterminator = new RuntimePolicyDeterminator(Configuration); 
            MetadataProvider = new MetadataProvider(Configuration);
            DisplayProvider = new DisplayProvider(Configuration, ActiveGlimpseRequestContexts);
            TabProvider = new TabProvider(Configuration, ActiveGlimpseRequestContexts);
            InspectorProvider = new InspectorProvider(Configuration, ActiveGlimpseRequestContexts);

            DisplayProvider.Setup();
            TabProvider.Setup();
            InspectorProvider.Setup();

            // TODO: This seems weird here
            PersistMetadata();
        }
        
        private void PersistMetadata()
        {
            var metadata = MetadataProvider.GetMetadata();

            Configuration.PersistenceStore.SaveMetadata(metadata);
        }
          
        internal static string CreateKey(object obj)
        {
            string result;
            var keyProvider = obj as IKey;

            if (keyProvider != null)
            {
                result = keyProvider.Key;
            }
            else
            {
                result = obj.GetType().FullName;
            }

            return result
                .Replace('.', '_')
                .Replace(' ', '_')
                .ToLower();
        }

        // TODO this should not be public! This was changed to hack in OWIN support
        // TODO do we need both GenerateScriptTags methods
#warning this should not be public! but we need to have some way to get to generate script tags conditionally so that they are only generated once (like glimpse injects it before </body> and at the same time a user has added the GlimpseClient control)
        public string GenerateScriptTags(GlimpseRequestContextHandle glimpseRequestContextHandle)
        {
            if (glimpseRequestContextHandle == null)
            {
                throw new ArgumentNullException("glimpseRequestContextHandle");
            }

            if (glimpseRequestContextHandle.RequestHandlingMode != RequestHandlingMode.RegularRequest)
            {
                return string.Empty;
            }

            IGlimpseRequestContext glimpseRequestContext;
            if (!TryGetRequestContext(glimpseRequestContextHandle.GlimpseRequestId, out glimpseRequestContext))
            {
                throw new GlimpseException("No corresponding GlimpseRequestContext found for GlimpseRequestId '" + glimpseRequestContextHandle.GlimpseRequestId + "'.");
            }

            return GenerateScriptTags(glimpseRequestContext);
        }

        // TODO do we need both GenerateScriptTags methods
#warning this should not be public! but we need to have some way to get to generate script tags conditionally so that they are only generated once (like glimpse injects it before </body> and at the same time a user has added the GlimpseClient control)
        public string GenerateScriptTags(IGlimpseRequestContext glimpseRequestContext)
        {
            if (glimpseRequestContext.CurrentRuntimePolicy == RuntimePolicy.Off)
            {
                return string.Empty;
            }

            var requestStore = glimpseRequestContext.RequestStore;
            var hasRendered = false;

            if (requestStore.Contains(Constants.ScriptsHaveRenderedKey))
            {
                hasRendered = requestStore.Get<bool>(Constants.ScriptsHaveRenderedKey);
            }

            if (hasRendered)
            {
                return string.Empty;
            }

            var glimpseScriptTags = GlimpseScriptTagsGenerator.Generate(glimpseRequestContext.GlimpseRequestId, Configuration);

            requestStore.Set(Constants.ScriptsHaveRenderedKey, true);
            return glimpseScriptTags;
        }
    }
}