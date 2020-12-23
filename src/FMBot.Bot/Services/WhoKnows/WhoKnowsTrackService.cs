using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Discord.Commands;
using FMBot.Bot.Models;
using FMBot.Persistence.Domain.Models;
using FMBot.Persistence.EntityFrameWork;
using Microsoft.EntityFrameworkCore;

namespace FMBot.Bot.Services.WhoKnows
{
    public class WhoKnowsTrackService
    {
        private readonly IDbContextFactory<FMBotDbContext> _contextFactory;
        private readonly SqlConnectionFactory _sqlConnectionFactory;

        public WhoKnowsTrackService(IDbContextFactory<FMBotDbContext> contextFactory, SqlConnectionFactory sqlConnectionFactory)
        {
            this._contextFactory = contextFactory;
            this._sqlConnectionFactory = sqlConnectionFactory;
        }

        public async Task<IList<WhoKnowsObjectWithUser>> GetIndexedUsersForTrack(ICommandContext context,
            ICollection<GuildUser> guildUsers, int guildId, string artistName, string trackName)
        {
            const string sql = "SELECT ut.user_id AS \"UserId\", " +
                               "ut.name AS \"Name\", " +
                               "ut.artist_name AS \"ArtistName\", " +
                               "ut.playcount AS \"Playcount\"," +
                               " u.user_name_last_fm AS \"UserNameLastFm\", " +
                               "u.discord_user_id AS \"DiscordUserId\" " +
                               "FROM user_tracks AS ut " +
                               "INNER JOIN users AS u ON ut.user_id = u.user_id " +
                               "INNER JOIN guild_users AS gu ON gu.user_id = u.user_id " +
                               "WHERE gu.guild_id = @guildId AND UPPER(ut.name) = UPPER('@trackName') AND UPPER(ut.artist_name) = UPPER('@artistName') " +
                               "ORDER BY ut.playcount DESC " +
                               "LIMIT 14";

            var connection = this._sqlConnectionFactory.GetOpenConnection();

            var userTracks = await connection.QueryAsync<WhoKnowsTrackDto>(sql, new
            {
                guildId,
                trackName,
                artistName
            });

            var whoKnowsTrackList = new List<WhoKnowsObjectWithUser>();

            foreach (var userTrack in userTracks)
            {
                var discordUser = await context.Guild.GetUserAsync(userTrack.DiscordUserId);
                var guildUser = guildUsers.FirstOrDefault(f => f.UserId == userTrack.UserId);
                var userName = discordUser != null ?
                    discordUser.Nickname ?? discordUser.Username :
                    guildUser?.UserName ?? userTrack.UserNameLastFm;

                whoKnowsTrackList.Add(new WhoKnowsObjectWithUser
                {
                    Name = $"{userTrack.ArtistName} - {userTrack.Name}",
                    DiscordName = userName,
                    Playcount = userTrack.Playcount,
                    LastFMUsername = userTrack.UserNameLastFm,
                    UserId = userTrack.UserId,
                });
            }

            return whoKnowsTrackList;
        }

        public async Task<IReadOnlyList<ListTrack>> GetTopTracksForGuild(IReadOnlyList<User> guildUsers,
            OrderType orderType)
        {
            var userIds = guildUsers.Select(s => s.UserId);

            await using var db = this._contextFactory.CreateDbContext();
            var query = db.UserTracks
                .AsQueryable()
                .Where(w => userIds.Contains(w.UserId))
                .GroupBy(g => new { g.ArtistName, g.Name });

            query = orderType == OrderType.Playcount ?
                query.OrderByDescending(o => o.Sum(s => s.Playcount)).ThenByDescending(o => o.Count()) :
                query.OrderByDescending(o => o.Count()).ThenByDescending(o => o.Sum(s => s.Playcount));

            return await query
                .Take(14)
                .Select(s => new ListTrack
                {
                    ArtistName = s.Key.ArtistName,
                    TrackName = s.Key.Name,
                    Playcount = s.Sum(su => su.Playcount),
                    ListenerCount = s.Count()
                })
                .ToListAsync();
        }

        public async Task<int> GetWeekTrackPlaycountForGuildAsync(IEnumerable<User> guildUsers, string trackName, string artistName)
        {
            var now = DateTime.UtcNow;
            var minDate = DateTime.UtcNow.AddDays(-7);

            var userIds = guildUsers.Select(s => s.UserId);

            await using var db = this._contextFactory.CreateDbContext();
            return await db.UserPlays
                .AsQueryable()
                .CountAsync(t =>
                    userIds.Contains(t.UserId) &&
                    t.TimePlayed.Date <= now.Date &&
                    t.TimePlayed.Date > minDate.Date &&
                    t.TrackName.ToLower() == trackName.ToLower() &&
                    t.ArtistName.ToLower() == artistName.ToLower()
                    );
        }
    }
}
