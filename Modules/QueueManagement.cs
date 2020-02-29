﻿using Discord;
using Discord.Commands;
using Discord.WebSocket;
using ELO.Extensions;
using ELO.Services;
using RavenBOT.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ELO.Modules
{
    public class QueueManagement : ReactiveBase
    {
        public static Dictionary<ulong, Dictionary<ulong, DateTime>> QueueDelays = new Dictionary<ulong, Dictionary<ulong, DateTime>>();

        public QueueManagement(LobbyService lobbyService, PremiumService premium)
        {
            LobbyService = lobbyService;
            Premium = premium;
        }

        public LobbyService LobbyService { get; }

        public PremiumService Premium { get; }

        [Command("Join", RunMode = RunMode.Sync)]
        [Alias("JoinLobby", "Join Lobby", "j", "sign", "play", "ready")]
        [Summary("Join the queue in the current lobby.")]
        public virtual async Task JoinLobbyAsync()
        {
            using (var db = new Database())
            {
                if (!(Context.User as SocketGuildUser).IsRegistered(out var player))
                {
                    await SimpleEmbedAsync("You must register in order to join a lobby.");
                    return;
                }

                var lobby = db.Lobbies.FirstOrDefault(x => x.ChannelId == Context.Channel.Id);
                if (lobby == null)
                {
                    await SimpleEmbedAsync("This channel is not a lobby.");
                    return;
                }

                var bans = db.Bans.Where(x => x.UserId == Context.User.Id && x.GuildId == Context.Guild.Id).ToArray().Where(x => !x.IsExpired).OrderByDescending(x => x.ExpiryTime).ToArray();
                if (bans.Length != 0)
                {
                    var latest = bans.First();
                    if (lobby.HideQueue)
                    {
                        await Context.Message.DeleteAsync();
                        await SimpleEmbedAndDeleteAsync($"You are still banned from matchmaking for another: {RavenBOT.Common.Extensions.GetReadableLength(latest.RemainingTime)}", Color.Red, TimeSpan.FromSeconds(5));
                        return;
                    }
                    await SimpleEmbedAsync($"{Context.User.Mention} - You are still banned from matchmaking for another: {RavenBOT.Common.Extensions.GetReadableLength(latest.RemainingTime)}", Color.Red);
                    return;
                }

                var queue = db.GetQueuedPlayers(Context.Guild.Id, Context.Channel.Id).ToList();

                //Not sure if this is actually needed.
                if (queue.Count >= lobby.PlayersPerTeam * 2)
                {
                    if (lobby.HideQueue)
                    {
                        await Context.Message.DeleteAsync();
                        await SimpleEmbedAndDeleteAsync("Queue is full, wait for teams to be chosen before joining.", Color.Red, TimeSpan.FromSeconds(5));
                        return;
                    }

                    //Queue will be reset after teams are completely picked.
                    await SimpleEmbedAsync($"{Context.User.Mention} - Queue is full, wait for teams to be chosen before joining.", Color.DarkBlue);
                    return;
                }

                var comp = db.GetOrCreateCompetition(Context.Guild.Id);
                if (!comp.AllowMultiQueueing)
                {
                    var queued = db.QueuedPlayers.Where(x => x.GuildId == Context.Guild.Id && x.UserId == Context.User.Id && x.ChannelId != Context.Channel.Id).ToArray();
                    if (queued.Length > 0)
                    {
                        var guildChannels = queued.Select(x => MentionUtils.MentionChannel(x.ChannelId));

                        if (lobby.HideQueue)
                        {
                            await Context.Message.DeleteAsync();
                            await SimpleEmbedAndDeleteAsync($"MultiQueuing is not enabled in this server.\nPlease leave: {string.Join("\n", guildChannels)}", Color.Red, TimeSpan.FromSeconds(5));
                            return;
                        }
                        await SimpleEmbedAsync($"{Context.User.Mention} - MultiQueuing is not enabled in this server.\nPlease leave: {string.Join("\n", guildChannels)}", Color.Red);
                        return;
                    }
                }

                if (lobby.MinimumPoints != null)
                {
                    if (player.Points < lobby.MinimumPoints)
                    {
                        if (lobby.HideQueue)
                        {
                            await Context.Message.DeleteAsync();
                            await SimpleEmbedAndDeleteAsync($"You need a minimum of {lobby.MinimumPoints} points to join this lobby.", Color.Red, TimeSpan.FromSeconds(5));
                            return;
                        }
                        await SimpleEmbedAsync($"{Context.User.Mention} - You need a minimum of {lobby.MinimumPoints} points to join this lobby.", Color.Red);
                        return;
                    }
                }

                var currentGame = db.GetLatestGame(lobby);
                if (currentGame != null)
                {
                    if (currentGame.GameState == GameState.Picking)
                    {
                        if (lobby.HideQueue)
                        {
                            await Context.Message.DeleteAsync();
                            await SimpleEmbedAndDeleteAsync("Current game is picking teams, wait until this is completed.", Color.DarkBlue, TimeSpan.FromSeconds(5));
                            return;
                        }
                        await SimpleEmbedAsync("Current game is picking teams, wait until this is completed.", Color.DarkBlue);
                        return;
                    }
                }

                if (queue.Any(x => x.UserId == Context.User.Id))
                {
                    if (lobby.HideQueue)
                    {
                        await Context.Message.DeleteAsync();

                        // await SimpleEmbedAndDeleteAsync("You are already queued.", Color.DarkBlue, TimeSpan.FromSeconds(5));
                        return;
                    }

                    // await SimpleEmbedAsync($"{Context.User.Mention} - You are already queued.", Color.DarkBlue);
                    return;
                }

                if (comp.RequeueDelay.HasValue)
                {
                    if (QueueDelays.ContainsKey(Context.Guild.Id))
                    {
                        var currentGuild = QueueDelays[Context.Guild.Id];
                        if (currentGuild.ContainsKey(Context.User.Id))
                        {
                            var currentUserLastJoin = currentGuild[Context.User.Id];
                            if (currentUserLastJoin + comp.RequeueDelay.Value > DateTime.UtcNow)
                            {
                                var remaining = currentUserLastJoin + comp.RequeueDelay.Value - DateTime.UtcNow;
                                if (lobby.HideQueue)
                                {
                                    await SimpleEmbedAndDeleteAsync($"You cannot requeue for another {RavenBOT.Common.Extensions.GetReadableLength(remaining)}", Color.Red);
                                    return;
                                }
                                await SimpleEmbedAsync($"{Context.User.Mention} - You cannot requeue for another {RavenBOT.Common.Extensions.GetReadableLength(remaining)}", Color.Red);
                                return;
                            }
                            else
                            {
                                currentUserLastJoin = DateTime.UtcNow;
                            }
                        }
                        else
                        {
                            currentGuild.Add(Context.User.Id, DateTime.UtcNow);
                        }
                    }
                    else
                    {
                        var newDict = new Dictionary<ulong, DateTime>();
                        newDict.Add(Context.User.Id, DateTime.UtcNow);
                        QueueDelays.Add(Context.Guild.Id, newDict);
                    }
                }

                db.QueuedPlayers.Add(new Models.QueuedPlayer
                {
                    UserId = Context.User.Id,
                    ChannelId = lobby.ChannelId,
                    GuildId = lobby.GuildId
                });
                if (queue.Count + 1 >= lobby.PlayersPerTeam * 2)
                {
                    db.SaveChanges();
                    await LobbyService.LobbyFullAsync(Context, lobby);
                    return;
                }
                else
                {
                    if (lobby.HideQueue)
                    {
                        await Context.Message.DeleteAsync();
                        await SimpleEmbedAsync($"A player has joined the queue. **[{queue.Count + 1}/{lobby.PlayersPerTeam * 2}]**");
                    }
                    else
                    {
                        if (Premium.IsPremium(Context.Guild.Id))
                        {
                            await SimpleEmbedAsync($"{player.GetDisplayNameSafe()} joined the queue. **[{queue.Count + 1}/{lobby.PlayersPerTeam * 2}]**", Color.Green);
                        }
                        else
                        {
                            await ReplyAsync("", false, new EmbedBuilder
                            {
                                Description = $"{player.GetDisplayNameSafe()} joined the queue. **[{queue.Count + 1}/{lobby.PlayersPerTeam * 2}]**\n" +
                                $"[Get Premium to remove ELO bot branding]({Premium.PremiumConfig.ServerInvite})",
                                Color = Color.Green
                            }.Build());
                        }
                    }
                }

                db.SaveChanges();
            }
        }

        [Command("Leave", RunMode = RunMode.Sync)]
        [Alias("LeaveLobby", "Leave Lobby", "l", "out", "unsign", "remove", "unready")]
        [Summary("Leave the queue in the current lobby.")]
        public virtual async Task LeaveLobbyAsync()
        {
            using (var db = new Database())
            {
                if (!(Context.User as SocketGuildUser).IsRegistered(out var player))
                {
                    await SimpleEmbedAsync("You're not registered.");
                    return;
                }

                var lobby = db.Lobbies.FirstOrDefault(x => x.ChannelId == Context.Channel.Id);
                if (lobby == null)
                {
                    await SimpleEmbedAsync("This channel is not a lobby.");
                    return;
                }

                var queue = db.GetQueuedPlayers(Context.Guild.Id, Context.Channel.Id).ToList();
                if (!queue.Any(x => x.UserId == Context.User.Id))
                {
                    if (lobby.HideQueue)
                    {
                        await Context.Message.DeleteAsync();
                        await SimpleEmbedAndDeleteAsync("You are not queued for the next game.", Color.DarkBlue, TimeSpan.FromSeconds(5));
                    }
                    await SimpleEmbedAsync("You are not queued for the next game.", Color.DarkBlue);
                }
                else
                {
                    var game = db.GetLatestGame(lobby);
                    if (game != null)
                    {
                        if (game.GameState == GameState.Picking)
                        {
                            await SimpleEmbedAsync("Lobby is currently picking teams. You cannot leave a queue while this is happening.", Color.Red);
                            return;
                        }
                    }

                    db.QueuedPlayers.Remove(queue.FirstOrDefault(x => x.UserId == Context.User.Id));
                    db.SaveChanges();

                    if (lobby.HideQueue)
                    {
                        await Context.Message.DeleteAsync();
                        await SimpleEmbedAsync($"Removed a player. **[{queue.Count - 1}/{lobby.PlayersPerTeam * 2}]**");
                        return;
                    }
                    await SimpleEmbedAsync($"Removed {player.GetDisplayNameSafe()} from queue. **[{queue.Count - 1}/{lobby.PlayersPerTeam * 2}]**", Color.DarkBlue);
                }
            }
        }
    }
}