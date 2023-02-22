﻿using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
namespace WeatherApp;

public class BffAuthenticationStateProvider : AuthenticationStateProvider
{
    private static readonly TimeSpan UserCacheRefreshInterval = TimeSpan.FromSeconds(60);
    private Timer _timer = null!;
    
    private readonly HttpClient _client;
    private readonly ILogger<BffAuthenticationStateProvider> _logger;

    private DateTimeOffset _userLastCheck = DateTimeOffset.FromUnixTimeSeconds(0);
    private ClaimsPrincipal _cachedUser = new(new ClaimsIdentity());

    public BffAuthenticationStateProvider(
        HttpClient client,
        ILogger<BffAuthenticationStateProvider> logger)
    {
        _client = client;
        _logger = logger;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var user = await GetUser();
        var state = new AuthenticationState(user);

        // checks periodically for a session state change and fires event
        // this causes a round trip to the server
        // adjust the period accordingly if that feature is needed
        if (user.Identity?.IsAuthenticated ?? false)
        {
            _logger.LogInformation("starting background check..");
            
            // ReSharper disable once AsyncVoidLambda
            _timer = new Timer(async _ =>
            {
                try
                {

                    var currentUser = await GetUser(false);
                    if (currentUser.Identity?.IsAuthenticated == false)
                    {
                        _logger.LogInformation("user logged out");
                        NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(currentUser)));
                        await _timer.DisposeAsync();
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error retrieving user info");
                }
            }, null, 1000, 5000);
        }

        return state;
    }

    private async ValueTask<ClaimsPrincipal> GetUser(bool useCache = true)
    {
        var now = DateTimeOffset.Now;
        if (useCache && now < _userLastCheck + UserCacheRefreshInterval)
        {
            _logger.LogDebug("Taking user from cache");
            return _cachedUser;
        }

        _logger.LogDebug("Fetching user");
        _cachedUser = await FetchUser();
        _userLastCheck = now;

        return _cachedUser;
    }

    record ClaimRecord(string Type, object Value);

    private async Task<ClaimsPrincipal> FetchUser()
    {
        try
        {
            _logger.LogInformation("Fetching user information.");
            
            List<ClaimRecord> claims = await _client.GetFromJsonAsync<List<ClaimRecord>>("whoami?slide=false") ?? new List<ClaimRecord>();

            if(!claims?.Any() ?? true)
                return new ClaimsPrincipal(new ClaimsIdentity());
            
            var identity = new ClaimsIdentity(
                nameof(BffAuthenticationStateProvider),
                ClaimTypes.NameIdentifier,
                "role");

            foreach (var claim in claims!)
            {
                identity.AddClaim(new Claim(claim.Type, claim.Value.ToString()!));
            }

            return new ClaimsPrincipal(identity);
            
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fetching user failed");
        }

        return new ClaimsPrincipal(new ClaimsIdentity());
    }
}