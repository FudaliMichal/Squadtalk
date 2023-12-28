using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using RestSharp;
using Shared.Communication;
using Shared.DTOs;
using Shared.Enums;
using Shared.Extensions;
using Shared.Models;
using Shared.Services;

namespace Squadtalk.Client.Services;

public class CommunicationManager : ICommunicationManager
{
    private readonly AuthenticationStateProvider _authenticationStateProvider;
    private readonly RestClient _restClient;
    private readonly ISignalrService _signalrService;
    private readonly ILogger<CommunicationManager> _logger;

    private TextChannel _currentChannel = GroupChat.GlobalChat;

    private readonly List<TextChannel> _allChannels = [];
    private readonly List<UserModel> _users = [];
    private readonly List<GroupChat> _groupChats = [];
    private readonly List<DirectMessageChannel> _directMessageChannels = [];

    public IReadOnlyList<UserModel> Users => _users;
    public IReadOnlyList<TextChannel> AllChannels => _allChannels;
    public IReadOnlyList<GroupChat> GroupChats => _groupChats;
    public IReadOnlyList<DirectMessageChannel> DirectMessageChannels => _directMessageChannels;

    public CommunicationManager(AuthenticationStateProvider authenticationStateProvider, RestClient restClient,
        ISignalrService signalrService, ILogger<CommunicationManager> logger)
    {
        _authenticationStateProvider = authenticationStateProvider;
        _restClient = restClient;
        _signalrService = signalrService;
        _logger = logger;

        _signalrService.UserConnected += userDto => UserConnected(userDto, false);
        _signalrService.UserDisconnected += UserDisconnected;
        _signalrService.ConnectedUsersReceived += ReceivedConnectedUsers;
        _signalrService.TextChannelsReceived += TextChannelsReceived;
        _signalrService.AddedToTextChannel += AddedToTextChannel;
    }

    public event Action? StateChanged;
    public event Func<Task>? StateChangedAsync;
    public event Action? ChannelChanged;
    public event Func<Task>? ChannelChangedAsync; 

    public TextChannel CurrentChannel => _currentChannel;

    public TextChannel? GetChannel(string id)
    {
        return id == GroupChat.GlobalChatId
            ? GroupChat.GlobalChat
            : AllChannels.FirstOrDefault(x => x.Id == id);
    }

    public Task ChangeChannelAsync(string channelId)
    {
        if (CurrentChannel.Id == channelId)
        {
            return Task.CompletedTask;
        }

        if (channelId == GroupChat.GlobalChatId)
        {
            return ChangeChannelAsync(GroupChat.GlobalChat);
        }

        var selectedChannel = GetChannel(channelId);
        if (selectedChannel is null)
        {
            _logger.LogWarning("Cannot find channel with id {Id}", channelId);
            return Task.CompletedTask;
        }

        return ChangeChannelAsync(selectedChannel);
    }

    public Task ChangeChannelAsync(TextChannel channel)
    {
        if (_currentChannel == channel)
        {
            return Task.CompletedTask;
        }

        _currentChannel.Selected = false;
        
        _currentChannel = channel;
        _currentChannel.Selected = true;
        _currentChannel.State.UnreadMessages = 0;
        
        ChannelChanged?.Invoke();
        return ChannelChangedAsync.TryInvoke();
    }

    public async Task OpenOrCreateFakeDirectMessageChannel(UserModel model)
    {
        var authenticationState = await _authenticationStateProvider.GetAuthenticationStateAsync();
        var id = authenticationState.User.GetRequiredClaimValue(ClaimTypes.NameIdentifier);
        
        if (id == model.Id) return;
        
        var openDirectMessageChannelWithUser = DirectMessageChannels.FirstOrDefault(x => x.Other.Id == model.Id);
        if (openDirectMessageChannelWithUser is not null)
        {
            await ChangeChannelAsync(openDirectMessageChannelWithUser);
            return;
        }
        
        await ChangeChannelAsync(DirectMessageChannel.CreateFakeChannel(model));
    }

    public async Task CreateRealDirectMessageChannel(TextChannel channel)
    {
        var authenticationState = await _authenticationStateProvider.GetAuthenticationStateAsync();
        var userId = authenticationState.User.GetRequiredClaimValue(ClaimTypes.NameIdentifier);

        var otherUserId = ((DirectMessageChannel) channel).Other.Id;
        var participants = new List<string> {userId, otherUserId};

        var channelId = await OpenNewChannel(participants);

        if (channelId is not null && GetChannel(channelId) is { } openedChannel)
        {
            await ChangeChannelAsync(openedChannel);
        }
    }

    private async Task<string?> OpenNewChannel(List<string> participants)
    {
        var request = new RestRequest("api/message/createChannel")
            .AddBody(participants);

        try
        {
            var createdChannelId = await _restClient.PostAsync<string>(request);
            return createdChannelId;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while sending create channel request");

            return null;
        }
    }

    private UserModel GetOrCreateUserModel(UserDto userDto)
    {
        var existingModel = _users.FirstOrDefault(x => x.Id == userDto.Id);

        if (existingModel is not null)
        {
            return existingModel;
        }
        
        var newModel = new UserModel
        {
            Username = userDto.Username,
            Id = userDto.Id,
            Status = UserStatus.Offline,
            Color = "black",
            AvatarUrl = "user.png"
        };
        
        _users.Add(newModel);

        return newModel;
    }

    private async Task AddedToTextChannel(ChannelDto dto)
    {
        var authenticationState = await _authenticationStateProvider.GetAuthenticationStateAsync();
        await AddChannel(dto, authenticationState, false);
        
        await StateChangedAsync.TryInvoke();
    }

    private async Task TextChannelsReceived(IEnumerable<ChannelDto> dtos)
    {
        var authenticationState = await _authenticationStateProvider.GetAuthenticationStateAsync();
        
        foreach (var channelDto in dtos)
        {
             await AddChannel(channelDto, authenticationState, true);
        }

        StateChanged?.Invoke();
        await StateChangedAsync.TryInvoke();
    }
    
    private async Task AddChannel(ChannelDto channelDto, AuthenticationState authenticationState, bool bulk)
    {
        if (_allChannels.Exists(x => x.Id == channelDto.Id)) return;
        
        var model = CreateChannelModel(channelDto, authenticationState);
        if (!bulk)
        {
            model.State.ReachedEnd = true;
        }
        
        _allChannels.Add(model);

        if (model is GroupChat groupChat)
        {
            _groupChats.Add(groupChat);
        }
        else
        {
            var directMessageChannel = (DirectMessageChannel) model;
            _directMessageChannels.Add(directMessageChannel);

            await CheckIfNeedToUpgradeCurrentFakeChannelToReal(directMessageChannel);
        }

        if (!bulk)
        {
            StateChanged?.Invoke();
            await StateChangedAsync.TryInvoke();
        }
    }

    private Task CheckIfNeedToUpgradeCurrentFakeChannelToReal(DirectMessageChannel openedDirectMessageChannel)
    {
        if (_currentChannel is DirectMessageChannel { Id: DirectMessageChannel.FakeChannelId } currentFakeDm &&
            currentFakeDm.Other.Id == openedDirectMessageChannel.Other.Id)
        {
            return ChangeChannelAsync(openedDirectMessageChannel);
        }
        
        return Task.CompletedTask;
    }
    
    private TextChannel CreateChannelModel(ChannelDto channelDto, AuthenticationState authenticationState)
    {
        var id = authenticationState.User.GetRequiredClaimValue(ClaimTypes.NameIdentifier);
        
        _logger.LogInformation("Creating model");
        
        var others = channelDto.Participants.Where(x => x.Id != id).ToList();
        if (others.Count > 1)
        {
            return new GroupChat(channelDto.Id)
            {
                Others = others.Select(GetOrCreateUserModel).ToList()
            };
        }

        var other = others[0];
        var user = GetOrCreateUserModel(other);
        
        var channel = new DirectMessageChannel(user, channelDto.Id);

        user.OpenChannel = channel;

        return channel;
    }

    private async Task ReceivedConnectedUsers(IEnumerable<UserDto> users)
    {
        foreach (var user in users)
        {
            _logger.LogInformation("User {@User} connected in bulk", user.Username);
            await UserConnected(user, true);
        }

        StateChanged?.Invoke();
        await StateChangedAsync.TryInvoke();
    }

    private async Task UserConnected(UserDto userDto, bool bulkAdd)
    {
        var authenticationState = await _authenticationStateProvider.GetAuthenticationStateAsync();
        if (userDto.Id == authenticationState.User.GetRequiredClaimValue(ClaimTypes.NameIdentifier))
        {
            return;
        }
        
        var model = GetOrCreateUserModel(userDto);
        model.Status = UserStatus.Online;

        if (!bulkAdd)
        {
            StateChanged?.Invoke();
            await StateChangedAsync.TryInvoke();
        }
    }

    private async Task UserDisconnected(UserDto userDto)
    {
        var authenticationState = await _authenticationStateProvider.GetAuthenticationStateAsync();
        if (userDto.Id == authenticationState.User.GetRequiredClaimValue(ClaimTypes.NameIdentifier)) return;

        var openDirectMessageChannelWithUser = DirectMessageChannels.FirstOrDefault(x => x.Other.Id == userDto.Id);
        if (openDirectMessageChannelWithUser is null)
        {
            _users.RemoveAll(x => x.Id == userDto.Id);
        }
        else
        {
            _users.First(x => x.Id == userDto.Id).Status = UserStatus.Offline;
        }

        StateChanged?.Invoke();
        await StateChangedAsync.TryInvoke();
    }
}