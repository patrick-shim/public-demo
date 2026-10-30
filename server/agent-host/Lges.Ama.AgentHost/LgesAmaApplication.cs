using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.A365.Observability.Hosting.Caching;
using Microsoft.Agents.A365.Observability.Runtime.Common;
using Microsoft.Agents.A365.Observability.Runtime.Tracing.Contracts;
using Microsoft.Agents.A365.Observability.Runtime.Tracing.Scopes;
using Microsoft.Agents.A365.Runtime.Authentication;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Purview;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Builder.State;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.Core.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Lges.Ama.Agent;
using ObservabilityRequest = Microsoft.Agents.A365.Observability.Runtime.Tracing.Contracts.Request;

namespace Lges.Ama.AgentHost;

public sealed partial class LgesAmaApplication : AgentApplication
{
    private readonly AgentChatClientFactory _chatClientFactory;
    private readonly InternalMcpToolCatalog _internalMcpTools;
    private readonly AgentTurnFrontendContext _frontendContext;
    private readonly AgentTurnCoordinator _turnCoordinator;
    private readonly AgentFrontendIdentityBinding _frontendIdentityBinding;
    private readonly AgentIdentityTokenContext _tokenContext;
    private readonly ToolUserContext _toolUserContext;
    private readonly IExporterTokenCache<string> _observabilityTokenCache;
    private readonly IAgentIdentityOboTokenExchange _oboTokenExchange;
    private readonly Agent365HostOptions _agent365Options;
    private readonly AgentHostOptions _agentHostOptions;
    private readonly PurviewDlpOptions _purviewDlpOptions;
    private readonly PromptShieldGuard _promptShieldGuard;
    private readonly PromptShieldOptions _promptShieldOptions;
    private readonly InternalMcpOptions _internalMcpOptions;
    private readonly AgentIdentityAuthorizationOptions _authorizationOptions;
    private readonly AgentIdentityOboOptions _oboOptions;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LgesAmaApplication> _logger;
    private readonly bool _isLocalEnvironment;

    public LgesAmaApplication(
        AgentApplicationOptions options,
        AgentChatClientFactory chatClientFactory,
        InternalMcpToolCatalog internalMcpTools,
        AgentTurnFrontendContext frontendContext,
        AgentTurnCoordinator turnCoordinator,
        AgentFrontendIdentityBinding frontendIdentityBinding,
        AgentIdentityTokenContext tokenContext,
        ToolUserContext toolUserContext,
        IExporterTokenCache<string> observabilityTokenCache,
        IAgentIdentityOboTokenExchange oboTokenExchange,
        IOptions<AgentHostOptions> agentHostOptions,
        IOptions<Agent365HostOptions> agent365Options,
        IOptions<PurviewDlpOptions> purviewDlpOptions,
        PromptShieldGuard promptShieldGuard,
        IOptions<PromptShieldOptions> promptShieldOptions,
        IOptions<InternalMcpOptions> internalMcpOptions,
        IOptions<AgentIdentityAuthorizationOptions> authorizationOptions,
        IOptions<AgentIdentityOboOptions> oboOptions,
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<LgesAmaApplication> logger)
        : base(options)
    {
        _chatClientFactory = chatClientFactory;
        _internalMcpTools = internalMcpTools;
        _frontendContext = frontendContext;
        _turnCoordinator = turnCoordinator;
        _frontendIdentityBinding = frontendIdentityBinding;
        _tokenContext = tokenContext;
        _toolUserContext = toolUserContext;
        _observabilityTokenCache = observabilityTokenCache;
        _oboTokenExchange = oboTokenExchange;
        _agentHostOptions = agentHostOptions.Value;
        _agent365Options = agent365Options.Value;
        _purviewDlpOptions = purviewDlpOptions.Value;
        _promptShieldGuard = promptShieldGuard;
        _promptShieldOptions = promptShieldOptions.Value;
        _internalMcpOptions = internalMcpOptions.Value;
        _authorizationOptions = authorizationOptions.Value;
        _oboOptions = oboOptions.Value;
        _configuration = configuration;
        _logger = logger;
        _isLocalEnvironment = environment.IsDevelopment() || environment.IsEnvironment("Playground");

        OnConversationUpdate(ConversationUpdateEvents.MembersAdded, WelcomeAsync);

        var agenticAuthHandlers = _isLocalEnvironment
            ? []
            : _authorizationOptions.AgenticUser.GetAutoSignInHandlerNames(
                _purviewDlpOptions.Enabled,
                _internalMcpOptions.Enabled && _internalMcpOptions.UseAuthentication);
        var oboAuthHandlers = _isLocalEnvironment
            ? []
            : _authorizationOptions.OnBehalfOf.GetAutoSignInHandlerNames(
                _purviewDlpOptions.Enabled,
                _internalMcpOptions.Enabled && _internalMcpOptions.UseAuthentication);
        OnActivity(
            ActivityTypes.Message,
            OnAgenticMessageAsync,
            isAgenticOnly: true,
            autoSignInHandlers: agenticAuthHandlers);
        OnActivity(
            ActivityTypes.Message,
            OnOboMessageAsync,
            isAgenticOnly: false,
            autoSignInHandlers: oboAuthHandlers);
    }

    private Task OnAgenticMessageAsync(
        ITurnContext turnContext,
        ITurnState turnState,
        CancellationToken cancellationToken) =>
        OnMessageAsync(
            turnContext,
            turnState,
            AgentFrontendMode.AgenticUser,
            cancellationToken);

    private Task OnOboMessageAsync(
        ITurnContext turnContext,
        ITurnState turnState,
        CancellationToken cancellationToken) =>
        OnMessageAsync(
            turnContext,
            turnState,
            AgentFrontendMode.OnBehalfOf,
            cancellationToken);

    private static async Task WelcomeAsync(
        ITurnContext turnContext,
        ITurnState turnState,
        CancellationToken cancellationToken)
    {
        foreach (var member in turnContext.Activity.MembersAdded ?? [])
        {
            if (member.Id != turnContext.Activity.Recipient?.Id)
            {
                await turnContext.SendActivityAsync(
                    MessageFactory.Text("안녕하세요. LG에너지솔루션의 뉴스, 주가, IR 자료를 공개 출처와 함께 찾아드립니다. 무엇이 궁금하신가요?"),
                    cancellationToken);
            }
        }
    }

    private async Task OnMessageAsync(
        ITurnContext turnContext,
        ITurnState turnState,
        AgentFrontendMode activityMode,
        CancellationToken cancellationToken)
    {
        AgentFrontendMode frontendMode;
        try
        {
            frontendMode = _frontendContext.ResolveForActivity(activityMode, _isLocalEnvironment);
        }
        catch (InvalidOperationException exception)
        {
            LogFrontendModeMismatch(_logger, exception.GetType().Name);
            await turnContext.SendActivityAsync(
                MessageFactory.Text(_authorizationOptions.FailureMessage),
                cancellationToken);
            return;
        }

        using var turnDiagnosticsScope = AgentTurnDiagnostics.BeginScope(
            _logger,
            frontendMode,
            turnContext.Activity);
        using var turnLease = await _turnCoordinator.EnterAsync(
            frontendMode,
            turnContext.Activity.Conversation?.Id,
            turnContext.Activity.Id,
            cancellationToken);
        if (turnLease.IsDuplicate)
        {
            LogDuplicateActivity(_logger, frontendMode);
            return;
        }

        var userText = turnContext.Activity.Text?.Trim();
        var inputRejection = AgentInputGuard.Evaluate(
            userText,
            _agentHostOptions.MaximumPromptCharacters);
        if (inputRejection == AgentInputRejection.Empty)
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text("LG에너지솔루션에 대해 궁금한 내용을 입력해 주세요."),
                cancellationToken);
            return;
        }
        if (inputRejection == AgentInputRejection.TooLong)
        {
            LogPromptRejected(_logger, _agentHostOptions.MaximumPromptCharacters, frontendMode);
            await turnContext.SendActivityAsync(
                MessageFactory.Text(
                    "Your request is too long. Shorten it and try again. Error code: STA-INPUT-001."),
                cancellationToken);
            return;
        }

        AgentIdentityTurnTokens? turnTokens;
        try
        {
            turnTokens = await ResolveTurnTokensAsync(
                turnContext,
                frontendMode,
                cancellationToken);
        }
        catch (Exception exception) when (!RequestCancellation.IsRequested(exception, cancellationToken))
        {
            await SendTurnFailureAsync(
                turnContext,
                frontendMode,
                "identity.resolve",
                exception,
                AgentFailureKind.Authentication,
                cancellationToken,
                _authorizationOptions.FailureMessage);
            return;
        }

        using IDisposable? tokenScope = turnTokens is null ? null : _tokenContext.Push(turnTokens);
        var agentBlueprintId = _configuration["Agent365Observability:AgentBlueprintId"];
        using IDisposable? purviewAgentMetadataScope =
            _purviewDlpOptions.Enabled && turnTokens is not null
                ? PurviewSerializationWorkaround.PushAgentMetadata(
                    agentBlueprintId
                        ?? throw new InvalidOperationException(
                            "Agent365Observability:AgentBlueprintId is required for Purview agent metadata."),
                    turnTokens.AgentId,
                    _purviewDlpOptions.AppName,
                    _purviewDlpOptions.AppVersion)
                : null;

        await turnContext.SendActivityAsync(Activity.CreateTypingActivity(), cancellationToken);

        // ==================== PROMPT SHIELDS: SCREEN THE USER PROMPT, FAIL CLOSED =================
        // Runs after the token scope is pushed so the guard can use the per-turn child identity
        // token, and before any model or tool call so a hijack attempt never reaches them.
        if (_promptShieldGuard.Enabled)
        {
            try
            {
                await _promptShieldGuard.EvaluateAsync(
                    userText ?? string.Empty,
                    PromptShieldSurface.UserPrompt,
                    cancellationToken);
            }
            catch (PromptShieldBlockedException)
            {
                LogPromptShieldBlocked(_logger, PromptShieldSurface.UserPrompt, frontendMode);
                await turnContext.SendActivityAsync(
                    MessageFactory.Text(_promptShieldOptions.BlockedPromptMessage),
                    cancellationToken);
                return;
            }
            catch (Exception exception) when (!RequestCancellation.IsRequested(exception, cancellationToken))
            {
                LogPromptShieldUnavailable(_logger, exception.GetType().Name);
                await turnContext.SendActivityAsync(
                    MessageFactory.Text(_promptShieldOptions.EvaluationFailureMessage),
                    cancellationToken);
                return;
            }
        }
        // ==================== END PROMPT SHIELDS ==================================================

        // ==================== PURVIEW DLP: BIND PROMPT TO THE HUMAN ENTRA USER ====================
        var userMessage = new ChatMessage(ChatRole.User, userText);
        var authenticatedUserId = turnTokens?.UserId ?? ResolveActivityUserId(turnContext.Activity);
        Guid? protectedUserId = null;
        if (_purviewDlpOptions.Enabled)
        {
            if (authenticatedUserId is not { } purviewUserId)
            {
                LogPurviewIdentityFailure(_logger);
                await turnContext.SendActivityAsync(
                    MessageFactory.Text(_purviewDlpOptions.EvaluationFailureMessage),
                    cancellationToken);
                return;
            }

            userMessage.SetUserId(purviewUserId);
            protectedUserId = purviewUserId;
        }

        userMessage.AdditionalProperties ??= [];
        userMessage.AdditionalProperties["conversationId"] =
            turnContext.Activity.Conversation?.Id ?? Guid.NewGuid().ToString();
        // ==================== END PURVIEW DLP =====================================================

        // ==================== A365 SDK: IDENTITY BAGGAGE ONLY; NEVER RECORD PROMPT CONTENT =========
        var conversationId = turnContext.Activity.Conversation?.Id;
        var channelName = turnContext.Activity.ChannelId;
        var callerId = authenticatedUserId?.ToString();
        var agenticUserId = frontendMode == AgentFrontendMode.AgenticUser
            ? turnContext.Activity.Recipient?.AgenticUserId
            : null;
        using IDisposable? agent365Baggage = turnTokens is not null
            ? new BaggageBuilder()
                .AgentId(turnTokens.AgentId)
                .TenantId(turnTokens.TenantId)
                .AgentBlueprintId(agentBlueprintId ?? string.Empty)
                .ConversationId(conversationId ?? string.Empty)
                .ChannelName(channelName ?? string.Empty)
                .UserId(callerId ?? string.Empty)
                .AgenticUserId(agenticUserId ?? string.Empty)
                .Build()
            : null;
        using var invokeAgentScope = CreateInvokeAgentScope(
            turnContext,
            turnTokens,
            agentBlueprintId,
            authenticatedUserId);
        // ==================== END A365 SDK ========================================================

        InternalMcpToolSession mcpSession;
        try
        {
            mcpSession = await _internalMcpTools.OpenAsync(cancellationToken);
        }
        catch (Exception exception) when (!RequestCancellation.IsRequested(exception, cancellationToken))
        {
            await SendTurnFailureAsync(
                turnContext,
                frontendMode,
                "mcp.discover",
                exception,
                AgentFailureKind.Unavailable,
                cancellationToken);
            return;
        }

        await using var ownedMcpSession = mcpSession;
        // Purview protection scopes and their ETag are turn-bound. Create a fresh wrapper for each
        // turn, while the factory defers disposal until host shutdown so background audit work can
        // finish without sharing policy state across requests.
        var chatClient = _chatClientFactory.Create();
        var agent = CompanyAgentFactory.Create(
            chatClient,
            ownedMcpSession.Tools,
            turnTokens?.AgentId);
        var conversationSessionKey = GetConversationSessionKey(frontendMode);
        AgentSession session;
        try
        {
            session = await GetSessionAsync(
                agent,
                turnState,
                conversationSessionKey,
                cancellationToken);
        }
        catch (Exception exception) when (!RequestCancellation.IsRequested(exception, cancellationToken))
        {
            await SendTurnFailureAsync(
                turnContext,
                frontendMode,
                "session.load",
                exception,
                AgentFailureKind.InvalidResponse,
                cancellationToken);
            return;
        }
        var response = new StringBuilder();
        var purviewBlocked = false;

        using IDisposable? toolUserScope = protectedUserId is { } userId
            ? _toolUserContext.Push(userId)
            : null;
        try
        {
            // ==================== PURVIEW DLP: REAL MIDDLEWARE PROTECTS INPUT AND BUFFERED OUTPUT ===
            await foreach (var update in agent.RunStreamingAsync(
                userMessage,
                session,
                cancellationToken: cancellationToken))
            {
                if (string.IsNullOrEmpty(update.Text))
                {
                    continue;
                }

                if (update.Role == ChatRole.Assistant)
                {
                    response.Append(update.Text);
                }
                else if (update.Role == ChatRole.System && IsPurviewBlockMessage(update.Text))
                {
                    purviewBlocked = true;
                    response.Append(update.Text);
                }
            }
            // ==================== END PURVIEW DLP ==================================================
        }
        catch (ToolContentBlockedException exception)
        {
            LogToolContentBlocked(_logger, exception.Direction);
            await turnContext.SendActivityAsync(
                MessageFactory.Text(
                    exception.Direction == ToolContentDirection.Arguments
                        ? _purviewDlpOptions.BlockedPromptMessage
                        : _purviewDlpOptions.BlockedResponseMessage),
                cancellationToken);
            return;
        }
        catch (ToolContentEvaluationException exception)
        {
            LogToolContentEvaluationFailure(_logger, exception.GetType().Name);
            await turnContext.SendActivityAsync(
                MessageFactory.Text(_purviewDlpOptions.EvaluationFailureMessage),
                cancellationToken);
            return;
        }
        catch (PurviewException exception)
        {
            // ==================== PURVIEW DLP: FAIL CLOSED; NO MODEL OUTPUT OR SESSION SAVE =========
            LogPurviewEvaluationFailure(_logger, exception.GetType().Name);
            await turnContext.SendActivityAsync(
                MessageFactory.Text(_purviewDlpOptions.EvaluationFailureMessage),
                cancellationToken);
            return;
            // ==================== END PURVIEW DLP ==================================================
        }
        catch (Exception exception) when (!RequestCancellation.IsRequested(exception, cancellationToken))
        {
            await SendTurnFailureAsync(
                turnContext,
                frontendMode,
                "agent.run",
                exception,
                AgentFailureKind.Internal,
                cancellationToken);
            return;
        }

        // A blocked response may contain sensitive model output in transient state; never persist it.
        if (!purviewBlocked)
        {
            try
            {
                await SaveSessionAsync(
                    agent,
                    session,
                    turnState,
                    conversationSessionKey,
                    cancellationToken);
            }
            catch (Exception exception) when (!RequestCancellation.IsRequested(exception, cancellationToken))
            {
                await SendTurnFailureAsync(
                    turnContext,
                    frontendMode,
                    "session.save",
                    exception,
                    AgentFailureKind.Internal,
                    cancellationToken);
                return;
            }
        }

        await turnContext.SendActivityAsync(
            MessageFactory.Text(response.Length == 0 ? "I could not produce a response for that request." : response.ToString()),
            cancellationToken);
    }

    private async Task<AgentIdentityTurnTokens?> ResolveTurnTokensAsync(
        ITurnContext turnContext,
        AgentFrontendMode frontendMode,
        CancellationToken cancellationToken)
    {
        var handlers = _authorizationOptions.GetHandlers(frontendMode);
        string tenantId;
        string resolvedAgentId;
        if (_isLocalEnvironment)
        {
            tenantId = turnContext.Activity.GetAgenticTenantId();
            if (!Guid.TryParse(tenantId, out _))
            {
                LogNoAgentIdentity(_logger);
                return null;
            }

            resolvedAgentId = frontendMode == AgentFrontendMode.AgenticUser
                ? turnContext.Activity.GetAgenticInstanceId()
                : _oboOptions.AgentId;
            if (frontendMode == AgentFrontendMode.AgenticUser
                && !Guid.TryParse(resolvedAgentId, out _))
            {
                LogNoAgentIdentity(_logger);
                return null;
            }
        }
        else
        {
            var identity = _frontendIdentityBinding.Resolve(turnContext.Activity, frontendMode);
            tenantId = identity.TenantId;
            resolvedAgentId = identity.AgentId;
        }

        var accessTokens = new Dictionary<string, string>(StringComparer.Ordinal);
        string foundryToken;
        string? oboUserAssertion = null;
        var userId = ResolveActivityUserId(turnContext.Activity);
        if (frontendMode == AgentFrontendMode.AgenticUser)
        {
            foundryToken = await GetRequiredAgenticTurnTokenAsync(
                turnContext,
                handlers.FoundryAuthHandlerName,
                AgentIdentityAuthorizationScopes.Foundry,
                resolvedAgentId,
                cancellationToken);
        }
        else
        {
            oboUserAssertion = await UserAuthorization.GetTurnTokenAsync(
                turnContext,
                handlers.FoundryAuthHandlerName,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(oboUserAssertion) && _isLocalEnvironment)
            {
                LogNoAgentIdentity(_logger);
                return null;
            }

            oboUserAssertion = RequireTurnToken(oboUserAssertion, handlers.FoundryAuthHandlerName);
            userId = AgentFrontendIdentityBinding.ValidateUserAssertion(
                oboUserAssertion,
                tenantId,
                turnContext.Activity.From?.AadObjectId);
            foundryToken = await _oboTokenExchange.ExchangeAsync(
                tenantId,
                resolvedAgentId,
                oboUserAssertion,
                [AgentIdentityAuthorizationScopes.Foundry],
                cancellationToken);
            if (_logger.IsEnabled(LogLevel.Information))
            {
                var childObjectIdMatches = TokenObjectIdMatches(foundryToken, resolvedAgentId);
                var userObjectIdMatches = TokenObjectIdMatches(
                    foundryToken,
                    userId?.ToString());
                LogOboFoundryTokenPrincipal(
                    _logger,
                    childObjectIdMatches,
                    userObjectIdMatches);
            }
        }

        accessTokens[AgentIdentityAuthorizationScopes.Foundry] = foundryToken;

        // ==================== PROMPT SHIELDS: SEPARATE CONTENT SAFETY AUDIENCE ====================
        // Requested independently of the Foundry audience so the guard does not depend on inference
        // staying inside Azure AI Foundry.
        if (_promptShieldOptions.Enabled)
        {
            accessTokens[AgentIdentityAuthorizationScopes.ContentSafety] =
                frontendMode == AgentFrontendMode.AgenticUser
                    ? await GetRequiredAgenticTurnTokenAsync(
                        turnContext,
                        handlers.FoundryAuthHandlerName,
                        AgentIdentityAuthorizationScopes.ContentSafety,
                        resolvedAgentId,
                        cancellationToken)
                    : await _oboTokenExchange.ExchangeAsync(
                        tenantId,
                        resolvedAgentId,
                        oboUserAssertion!,
                        [AgentIdentityAuthorizationScopes.ContentSafety],
                        cancellationToken);
        }
        // ==================== END PROMPT SHIELDS ==================================================

        if (_purviewDlpOptions.Enabled)
        {
            var purviewToken = frontendMode == AgentFrontendMode.AgenticUser
                ? await GetRequiredAgenticTurnTokenAsync(
                    turnContext,
                    handlers.PurviewAuthHandlerName,
                    AgentIdentityAuthorizationScopes.PurviewDelegated,
                    resolvedAgentId,
                    cancellationToken)
                : await _oboTokenExchange.ExchangeAsync(
                    tenantId,
                    resolvedAgentId,
                    oboUserAssertion!,
                    [AgentIdentityAuthorizationScopes.GraphDefault],
                    cancellationToken);
            accessTokens[AgentIdentityAuthorizationScopes.Purview] = purviewToken;
            accessTokens[AgentIdentityAuthorizationScopes.PurviewProtectionScopes] = purviewToken;
            accessTokens[AgentIdentityAuthorizationScopes.PurviewContentActivity] = purviewToken;
            accessTokens[AgentIdentityAuthorizationScopes.GraphDefault] = purviewToken;
        }

        if (_internalMcpOptions.Enabled && _internalMcpOptions.UseAuthentication)
        {
            var mcpScope = _internalMcpOptions.GetDelegatedScope();
            accessTokens[mcpScope] = frontendMode == AgentFrontendMode.AgenticUser
                ? await GetRequiredAgenticTurnTokenAsync(
                    turnContext,
                    handlers.InternalMcpAuthHandlerName,
                    mcpScope,
                    resolvedAgentId,
                    cancellationToken)
                : await _oboTokenExchange.ExchangeAsync(
                    tenantId,
                    resolvedAgentId,
                    oboUserAssertion!,
                    [AgentIdentityAuthorizationScopes.InternalMcpDefault(_internalMcpOptions.Audience)],
                    cancellationToken);
        }

        // ==================== A365 SDK: REGISTER PER-TURN OBSERVABILITY TOKEN CONTEXT ===============
        try
        {
            string[] observabilityScopes =
                [AgentIdentityAuthorizationScopes.ObservabilityDefault];
            var observabilityToken = await _oboTokenExchange.AcquireAppTokenAsync(
                tenantId,
                resolvedAgentId,
                observabilityScopes,
                cancellationToken);
            _observabilityTokenCache.RegisterObservability(
                resolvedAgentId,
                tenantId,
                observabilityToken,
                observabilityScopes);
        }
        catch (Exception exception) when (!RequestCancellation.IsRequested(exception, cancellationToken))
        {
            LogAgent365ObservabilityRegistrationFailure(_logger, exception.GetType().Name);
        }
        // ==================== END A365 SDK =========================================================

        // ==================== A365 SDK: WORKIQ SAFETY GATE ==========================================
        // Purview chat middleware protects textual prompts/responses, not MCP tool arguments/results.
        // WorkIQ must remain disabled until Agent 365 Tooling supports MCP 2.1 and generated tools
        // pass equivalent tool-content protection.
        if (!_agent365Options.EnableWorkIq)
        {
            LogWorkIqDisabled(_logger);
        }
        // ==================== END A365 SDK ==========================================================

        return new AgentIdentityTurnTokens(resolvedAgentId, tenantId, userId, accessTokens);
    }

    private static InvokeAgentScope? CreateInvokeAgentScope(
        ITurnContext turnContext,
        AgentIdentityTurnTokens? turnTokens,
        string? agentBlueprintId,
        Guid? authenticatedUserId)
    {
        if (turnTokens is null || !Guid.TryParse(agentBlueprintId, out _))
        {
            return null;
        }

        var conversationId = turnContext.Activity.Conversation?.Id ?? string.Empty;
        var channelName = turnContext.Activity.ChannelId ?? string.Empty;
        var caller = turnContext.Activity.From;
        var callerDetails = new CallerDetails(
            new UserDetails(
                userId: authenticatedUserId?.ToString()
                    ?? caller?.AadObjectId
                    ?? caller?.Id
                    ?? string.Empty,
                userEmail: string.Empty,
                userName: caller?.Name ?? string.Empty));
        var agentDetails = new AgentDetails(
            agentId: turnTokens.AgentId,
            agentName: "LG Energy Solution AMA",
            agentDescription: "LG Energy Solution public company research demo",
            agentBlueprintId: agentBlueprintId,
            tenantId: turnTokens.TenantId);
        var request = new ObservabilityRequest(
            content: string.Empty,
            sessionId: conversationId,
            channel: new Channel(channelName),
            conversationId: conversationId);
        var endpoint = new Uri($"https://{agentBlueprintId}.agent.invalid/");

        return InvokeAgentScope.Start(
            request,
            new InvokeAgentScopeDetails(endpoint),
            agentDetails,
            callerDetails);
    }

    private static Guid? ResolveActivityUserId(IActivity activity) =>
        Guid.TryParse(activity.From?.AadObjectId, out var userId)
            ? userId
            : null;

    private async Task<string> GetRequiredAgenticTurnTokenAsync(
        ITurnContext turnContext,
        string authHandlerName,
        string scope,
        string expectedAgentId,
        CancellationToken cancellationToken) =>
        await GetRequiredAgenticTurnTokenAsync(
            turnContext,
            authHandlerName,
            [scope],
            expectedAgentId,
            cancellationToken);

    private async Task<string> GetRequiredAgenticTurnTokenAsync(
        ITurnContext turnContext,
        string authHandlerName,
        string[] scopes,
        string expectedAgentId,
        CancellationToken cancellationToken)
    {
        var tokenTask = AgenticAuthenticationService.GetAgenticUserTokenAsync(
            UserAuthorization,
            authHandlerName,
            turnContext,
            scopes);
        var token = RequireTurnToken(
            await tokenTask.WaitAsync(cancellationToken),
            authHandlerName);
        AgentFrontendIdentityBinding.ValidateAgentToken(token, expectedAgentId);
        return token;
    }

    private static string RequireTurnToken(string? token, string authHandlerName) =>
        string.IsNullOrWhiteSpace(token)
            ? throw new InvalidOperationException(
                $"Auth handler '{authHandlerName}' returned no token for its configured resource.")
            : token;

    private bool IsPurviewBlockMessage(string text) =>
        _purviewDlpOptions.Enabled
        && (string.Equals(text, _purviewDlpOptions.BlockedPromptMessage, StringComparison.Ordinal)
            || string.Equals(text, _purviewDlpOptions.BlockedResponseMessage, StringComparison.Ordinal));

    private static async Task<AgentSession> GetSessionAsync(
        AIAgent agent,
        ITurnState turnState,
        string conversationSessionKey,
        CancellationToken cancellationToken)
    {
        var serialized = turnState.Conversation.GetValue<string?>(conversationSessionKey, () => null);
        if (string.IsNullOrWhiteSpace(serialized))
        {
            return await agent.CreateSessionAsync(cancellationToken);
        }

        var element = ProtocolJsonSerializer.ToObject<JsonElement>(serialized);
        return await agent.DeserializeSessionAsync(element, cancellationToken: cancellationToken);
    }

    private static async Task SaveSessionAsync(
        AIAgent agent,
        AgentSession session,
        ITurnState turnState,
        string conversationSessionKey,
        CancellationToken cancellationToken)
    {
        var serialized = await agent.SerializeSessionAsync(
            session,
            cancellationToken: cancellationToken);
        turnState.Conversation.SetValue(conversationSessionKey, ProtocolJsonSerializer.ToJson(serialized));
    }

    private static string GetConversationSessionKey(AgentFrontendMode frontendMode) =>
        frontendMode switch
        {
            AgentFrontendMode.AgenticUser => "conversation.agentSession.agenticUser",
            AgentFrontendMode.OnBehalfOf => "conversation.agentSession.obo",
            _ => throw new ArgumentOutOfRangeException(
                nameof(frontendMode),
                frontendMode,
                "Unknown frontend mode.")
        };

    private static bool TokenObjectIdMatches(string token, string? expectedObjectId)
    {
        if (string.IsNullOrWhiteSpace(expectedObjectId))
        {
            return false;
        }

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        var objectId = jwt.Claims
            .FirstOrDefault(claim => claim.Type is "oid"
                or "http://schemas.microsoft.com/identity/claims/objectidentifier")
            ?.Value;
        return string.Equals(objectId, expectedObjectId, StringComparison.OrdinalIgnoreCase);
    }

    private async Task SendTurnFailureAsync(
        ITurnContext turnContext,
        AgentFrontendMode frontendMode,
        string operation,
        Exception exception,
        AgentFailureKind fallback,
        CancellationToken cancellationToken,
        string? preferredMessage = null)
    {
        var failure = AgentFailureClassifier.Classify(exception, fallback);
        var exceptionType = exception.GetType().Name;
        if (failure.LogLevel == LogLevel.Error)
        {
            LogInternalTurnFailure(
                _logger,
                operation,
                failure.Code,
                failure.Kind,
                frontendMode,
                exceptionType);
        }
        else
        {
            LogRecoverableTurnFailure(
                _logger,
                operation,
                failure.Code,
                failure.Kind,
                frontendMode,
                exceptionType);
        }

        var usePreferredMessage = (failure.Kind is AgentFailureKind.Authentication
            or AgentFailureKind.Authorization)
            && !string.IsNullOrWhiteSpace(preferredMessage);
        var message = usePreferredMessage ? preferredMessage! : failure.UserMessage;
        await turnContext.SendActivityAsync(
            MessageFactory.Text($"{message} Error code: {failure.Code}."),
            cancellationToken);
    }

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Debug,
        Message = "No Agent 365 identity is available in local development; using local developer authentication.")]
    private static partial void LogNoAgentIdentity(ILogger logger);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Warning,
        Message = "Purview DLP requires a valid human Entra object ID; the turn was rejected.")]
    private static partial void LogPurviewIdentityFailure(ILogger logger);

    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Error,
        Message = "Purview DLP evaluation failed closed; the turn was rejected. exceptionType={ExceptionType}.")]
    private static partial void LogPurviewEvaluationFailure(ILogger logger, string exceptionType);

    [LoggerMessage(
        EventId = 1005,
        Level = LogLevel.Warning,
        Message = "Agent 365 observability token context could not be registered. exceptionType={ExceptionType}.")]
    private static partial void LogAgent365ObservabilityRegistrationFailure(
        ILogger logger,
        string exceptionType);

    [LoggerMessage(
        EventId = 1006,
        Level = LogLevel.Debug,
        Message = "Agent 365 WorkIQ tool loading is disabled until tooling supports MCP 2.1 and generated tools pass equivalent tool-content protection.")]
    private static partial void LogWorkIqDisabled(ILogger logger);

    [LoggerMessage(
        EventId = 1009,
        Level = LogLevel.Warning,
        Message = "Prompt Shields blocked the turn. surface={Surface} frontendMode={FrontendMode}.")]
    private static partial void LogPromptShieldBlocked(
        ILogger logger,
        PromptShieldSurface surface,
        AgentFrontendMode frontendMode);

    [LoggerMessage(
        EventId = 1010,
        Level = LogLevel.Error,
        Message = "Prompt Shields evaluation failed closed; the turn was rejected. exceptionType={ExceptionType}.")]
    private static partial void LogPromptShieldUnavailable(ILogger logger, string exceptionType);

    [LoggerMessage(
        EventId = 1007,
        Level = LogLevel.Warning,
        Message = "Purview blocked protected tool {direction}; the turn was rejected.")]
    private static partial void LogToolContentBlocked(
        ILogger logger,
        ToolContentDirection direction);

    [LoggerMessage(
        EventId = 1008,
        Level = LogLevel.Error,
        Message = "Tool content evaluation failed closed; the turn was rejected. exceptionType={ExceptionType}.")]
    private static partial void LogToolContentEvaluationFailure(
        ILogger logger,
        string exceptionType);

    [LoggerMessage(
        EventId = 1010,
        Level = LogLevel.Warning,
        Message = "The protected turn frontend did not match its Agent Framework activity shape. exceptionType={ExceptionType}.")]
    private static partial void LogFrontendModeMismatch(
        ILogger logger,
        string exceptionType);

    [LoggerMessage(
        EventId = 1011,
        Level = LogLevel.Information,
        Message = "Foundry OBO token principal classification: child={ChildMatch}, user={UserMatch}.",
        SkipEnabledCheck = true)]
    private static partial void LogOboFoundryTokenPrincipal(
        ILogger logger,
        bool childMatch,
        bool userMatch);

    [LoggerMessage(
        EventId = 1101,
        Level = LogLevel.Warning,
        Message = "Turn operation {Operation} failed safely: code={FailureCode}, kind={FailureKind}, frontend={FrontendMode}, exceptionType={ExceptionType}.")]
    private static partial void LogRecoverableTurnFailure(
        ILogger logger,
        string operation,
        string failureCode,
        AgentFailureKind failureKind,
        AgentFrontendMode frontendMode,
        string exceptionType);

    [LoggerMessage(
        EventId = 1102,
        Level = LogLevel.Error,
        Message = "Turn operation {Operation} failed unexpectedly: code={FailureCode}, kind={FailureKind}, frontend={FrontendMode}, exceptionType={ExceptionType}.")]
    private static partial void LogInternalTurnFailure(
        ILogger logger,
        string operation,
        string failureCode,
        AgentFailureKind failureKind,
        AgentFrontendMode frontendMode,
        string exceptionType);

    [LoggerMessage(
        EventId = 1103,
        Level = LogLevel.Information,
        Message = "A duplicate activity was suppressed before agent execution. frontend={FrontendMode}.")]
    private static partial void LogDuplicateActivity(
        ILogger logger,
        AgentFrontendMode frontendMode);

    [LoggerMessage(
        EventId = 1104,
        Level = LogLevel.Warning,
        Message = "Prompt text exceeded the configured character limit and was rejected before policy/model execution. maximumCharacters={MaximumCharacters}, frontend={FrontendMode}.")]
    private static partial void LogPromptRejected(
        ILogger logger,
        int maximumCharacters,
        AgentFrontendMode frontendMode);
}
