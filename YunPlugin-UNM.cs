using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nini.Config;
using TS3AudioBot;
using TS3AudioBot.Audio;
using TS3AudioBot.CommandSystem;
using TS3AudioBot.Plugins;
using TSLib.Full;
using NeteaseCloudMusicApi;
using System.Text.RegularExpressions;
using System.Linq;

public class YunPlugin : IBotPlugin 
{
    //===========================================初始化===========================================
    static IConfigSource MyIni;
    PlayManager tempplayManager;
    InvokerData tempinvoker;
    Ts3Client tempts3Client;
    public static string cookies;
    public static int playMode;
    public static string WangYiYunAPI_Address;
    public static string UNM_Address;
    List<long> playlist = new List<long>();
    public static int Playlocation = 0;
    private readonly SemaphoreSlim playlock = new SemaphoreSlim(1, 1);
    private readonly SemaphoreSlim Listeninglock = new SemaphoreSlim(1, 1);
    public void Initialize()
    {
        string iniFilePath;
        
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Console.WriteLine("运行在Windows环境.");
            iniFilePath = "plugins/YunSettings.ini"; // Windows 文件目录
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            string dockerEnvFilePath = "/.dockerenv";

            if (File.Exists(dockerEnvFilePath))
            {
                Console.WriteLine("运行在Docker环境.");
            }
            else
            {
                Console.WriteLine("运行在Linux环境.");
            }

            string location = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
            iniFilePath = File.Exists(dockerEnvFilePath) ? location + "/data/plugins/YunSettings.ini" : location + "./plugins/YunSettings.ini";
        }
        else
        {
            throw new NotSupportedException("不支持的操作系统");
        }

        Console.WriteLine(iniFilePath);
        MyIni = new IniConfigSource(iniFilePath);

        playMode = int.TryParse(MyIni.Configs["YunBot"].Get("playMode"), out int playModeValue) ? playModeValue : 0;

        string cookiesValue = MyIni.Configs["YunBot"].Get("cookies1");
        cookies = string.IsNullOrEmpty(cookiesValue) ? "" : cookiesValue;

        string wangYiYunAPI_AddressValue = MyIni.Configs["YunBot"].Get("WangYiYunAPI_Address");
        WangYiYunAPI_Address = string.IsNullOrEmpty(wangYiYunAPI_AddressValue) ? "http://127.0.0.1:3000" : wangYiYunAPI_AddressValue;

        string unmAddressValue = MyIni.Configs["YunBot"].Get("UNM_Address");
        UNM_Address = string.IsNullOrEmpty(unmAddressValue) ? "" : unmAddressValue;

        Console.WriteLine(playMode);
        Console.WriteLine(cookies);
        Console.WriteLine(WangYiYunAPI_Address);
        Console.WriteLine(UNM_Address);

    }

    

    public void SetPlplayManager(PlayManager playManager)
    {
        tempplayManager = playManager;
    }
    public PlayManager GetplayManager()
    {
        return tempplayManager;
    }

    public InvokerData Getinvoker()
    {
        return tempinvoker;
    }

    public void SetInvoker(InvokerData invoker)
    {
        tempinvoker = invoker;
    }

    public void SetTs3Client(Ts3Client ts3Client)
    {
        tempts3Client = ts3Client;
    }

    public Ts3Client GetTs3Client()
    {
        return tempts3Client;
    }
    
    //===========================================初始化===========================================

    //===========================================播放模式===========================================
    [Command("yun mode")]
    public string Playmode(int mode)
    {
        if (mode >= 0 && mode <= 3)
        {
            playMode = mode;
            MyIni.Configs["YunBot"].Set("playMode", mode.ToString());
            MyIni.Save();
            return mode switch
            {
                0 => "顺序播放",
                1 => "顺序循环",
                2 => "随机播放",
                3 => "随机循环",
                _ => "未知播放模式",
            };
        }
        else
        {
            return "请输入正确的播放模式(0 到 3 之间的整数)";
        }
    }
    //===========================================播放模式===========================================

    //=============================================搜索=============================================
    [Command("yun s")]
    public async Task CommandYunS(string arguments, PlayManager playManager, InvokerData invoker, Ts3Client ts3Client)
    {
        SetInvoker(invoker);
        SetTs3Client(ts3Client);

        string[] splitArguments = arguments.Split("#");
        Console.WriteLine($"参数长度: {splitArguments.Length}");

        // 处理搜索请求（单个参数：搜索关键词）
        if (splitArguments.Length == 1)
        {
            await HandleSearchRequest(splitArguments[0], ts3Client, 1); // 默认第一页
        }
        // 处理搜索请求带页码（搜索关键词#页码）
        else if (splitArguments.Length == 2)
        {
            if (int.TryParse(splitArguments[1], out int page))
            {
                await HandleSearchRequest(splitArguments[0], ts3Client, page);
            }
            else
            {
                await SendErrorMessage(ts3Client, "请输入有效的页码数字");
            }
        }
        // 处理歌曲选择（搜索关键词#页码#歌曲序号）
        else if (splitArguments.Length == 3)
        {
            if (!int.TryParse(splitArguments[1], out int page))
            {
                await SendErrorMessage(ts3Client, "请输入有效的页码数字");
                return;
            }

            if (!int.TryParse(splitArguments[2], out int songIndex))
            {
                await SendErrorMessage(ts3Client, "请输入有效的歌曲序号");
                return;
            }

            await HandleSongSelection(splitArguments[0], page, songIndex, ts3Client, playManager, invoker);
        }
        else
        {
            await SendErrorMessage(ts3Client, "格式错误。使用: !yun s 关键词 或 !yun s 关键词#页码 或 !yun s 关键词#页码#歌曲序号");
        }
    }

    private async Task HandleSearchRequest(string searchTerm, Ts3Client ts3Client, int page)
    {
        if (page < 1)
        {
            await SendErrorMessage(ts3Client, "页码必须大于0");
            return;
        }

        const int pageSize = 6; // 每页显示6首歌曲
        int offset = (page - 1) * pageSize;

        string urlSearch = $"{WangYiYunAPI_Address}/search?keywords={searchTerm}&limit={pageSize}&offset={offset}";
        Console.WriteLine($"搜索URL: {urlSearch}");

        string searchJson = await HttpGetAsync(urlSearch);
        yunSearchSong yunSearchSong = JsonSerializer.Deserialize<yunSearchSong>(searchJson);

        // 检查API响应代码和结果
        if (yunSearchSong?.result?.songs == null || yunSearchSong.result.songCount == 0)
        {
            await SendErrorMessage(ts3Client, "未找到结果");
            return;
        }

        string message = BuildSearchResultMessage(yunSearchSong, page, searchTerm);
        await ts3Client.SendChannelMessage(message);
    }

    private async Task HandleSongSelection(string searchTerm, int page, int songIndex, Ts3Client ts3Client, PlayManager playManager, InvokerData invoker)
    {
        if (songIndex < 1 || songIndex > 6)
        {
            await SendErrorMessage(ts3Client, "请输入有效的歌曲序号 (1-6)");
            return;
        }

        const int pageSize = 6; // 每页显示6首歌曲
        int offset = (page - 1) * pageSize;

        string urlSearch = $"{WangYiYunAPI_Address}/search?keywords={searchTerm}&limit={pageSize}&offset={offset}";
        Console.WriteLine($"搜索URL: {urlSearch}");

        string searchJson = await HttpGetAsync(urlSearch);
        yunSearchSong yunSearchSong = JsonSerializer.Deserialize<yunSearchSong>(searchJson);

        // 检查API响应结果
        if (yunSearchSong?.result?.songs == null || yunSearchSong.result.songs.Count == 0)
        {
            await SendErrorMessage(ts3Client, "搜索失败或未找到结果");
            return;
        }

        int actualIndex = songIndex - 1;
        if (actualIndex >= yunSearchSong.result.songs.Count)
        {
            await SendErrorMessage(ts3Client, "选择的歌曲不存在");
            return;
        }

        await ProcessSong(yunSearchSong.result.songs[actualIndex].id, ts3Client, playManager, invoker);
    }

    private string BuildSearchResultMessage(yunSearchSong searchResult, int currentPage, string searchTerm)
    {
        var result = searchResult.result;
        var displayCount = result.songs.Count;
        var totalSongs = (int)result.songCount;

        const int pageSize = 6;
        int totalPages = (int)Math.Ceiling((double)totalSongs / pageSize);
        int startNumber = (currentPage - 1) * pageSize + 1;

        var messageBuilder = new StringBuilder($"搜索结果（第{currentPage}/{totalPages}页）:\n\n");

        for (int i = 0; i < displayCount; i++)
        {
            var song = result.songs[i];
            string artistName = song.artists.Count > 0 ? song.artists[0].name : "未知歌手";

            messageBuilder.AppendLine($"#{startNumber + i}：");
            messageBuilder.AppendLine($"歌曲名称：{song.name}");
            messageBuilder.AppendLine($"歌手：{artistName}");

            if (i < displayCount - 1)
                messageBuilder.AppendLine();
        }

        // 添加页码信息 - 修复：使用传入的searchTerm变量
        messageBuilder.AppendLine($"\n使用 !yun s {searchTerm} #{currentPage + 1} 查看下一页");
        if (currentPage > 1)
        {
            messageBuilder.AppendLine($"使用 !yun s {searchTerm} #{currentPage - 1} 查看上一页");
        }
        messageBuilder.AppendLine($"使用 !yun s {searchTerm} #{currentPage} #歌曲序号 选择歌曲");

        return messageBuilder.ToString();
    }

    private async Task SendErrorMessage(Ts3Client ts3Client, string message)
    {
        Console.WriteLine($"错误: {message}");
        await ts3Client.SendChannelMessage($"错误: {message}");
    }
    //=============================================搜索=============================================


    //===========================================单曲播放===========================================
    [Command("yun play")]
    public async Task CommandYunPlay(string arguments, PlayManager playManager, InvokerData invoker, Ts3Client ts3Client)
    {
        //playlist.Clear();
        SetInvoker(invoker);
        SetPlplayManager(playManager);
        SetTs3Client(ts3Client);
        bool songFound = false;
        string urlSearch = $"{WangYiYunAPI_Address}/search?keywords={arguments}&limit=30";
        string searchJson = await HttpGetAsync(urlSearch);
        yunSearchSong yunSearchSong = JsonSerializer.Deserialize<yunSearchSong>(searchJson);
        string[] splitArguments = arguments.Split(" - ");
        Console.WriteLine(splitArguments.Length);
        if (splitArguments.Length == 1)
        {
            _ = ProcessSong(yunSearchSong.result.songs[0].id, ts3Client, playManager, invoker);
            songFound = true;
        }
        else if (splitArguments.Length == 2)
        {
            // 歌曲名称和歌手
            string songName = splitArguments[0];
            string artist = splitArguments[1];

            for (int s = 0; s < yunSearchSong.result.songs.Count; s++)
            {
                if (yunSearchSong.result.songs[s].name == songName && yunSearchSong.result.songs[s].artists[0].name == artist)
                {
                    _ = ProcessSong(yunSearchSong.result.songs[s].id, ts3Client, playManager, invoker);
                    songFound = true;
                    break;
                }
            }
        }
        else
        {
            // 输入为空或格式不符合预期
            Console.WriteLine("请输入有效的歌曲信息");
            _ = ts3Client.SendChannelMessage("请输入有效的歌曲信息");
        }
        Playlocation = songFound && Playlocation > 0 ? Playlocation - 1 : Playlocation;
        if (!songFound)
        {
            _ = ts3Client.SendChannelMessage("未找到歌曲");
        }
    }

    //===========================================单曲播放===========================================

    //==========================================单曲id播放==========================================
    [Command("yun id")]
    public async Task CommandYunid(string arguments, PlayManager playManager, InvokerData invoker, Ts3Client ts3Client)
    {
        SetInvoker(invoker);
        SetPlplayManager(playManager);
        SetTs3Client(ts3Client);

        // 提取歌曲ID（支持直接输入ID或包含ID的URL）
        string songId = ExtractSongIdFromInput(arguments);

        if (string.IsNullOrEmpty(songId))
        {
            Console.WriteLine("未找到有效的歌曲ID");
            await ts3Client.SendChannelMessage("请输入有效的歌曲ID或网易云音乐链接");
            return;
        }

        Console.WriteLine($"提取的歌曲ID: {songId}");

        try
        {
            string urlSearch = $"{WangYiYunAPI_Address}/song/detail?ids={songId}";
            string searchJson = await HttpGetAsync(urlSearch);

            if (string.IsNullOrEmpty(searchJson))
            {
                Console.WriteLine("API请求失败");
                await ts3Client.SendChannelMessage("获取歌曲信息失败，请检查网络连接");
                return;
            }

            MusicDetail musicDetail = JsonSerializer.Deserialize<MusicDetail>(searchJson);

            if (musicDetail == null || musicDetail.songs == null || musicDetail.songs.Count == 0)
            {
                Console.WriteLine("歌曲信息解析失败");
                await ts3Client.SendChannelMessage("歌曲信息解析失败");
                return;
            }

            long parsedSongId;
            if (!long.TryParse(songId, out parsedSongId))
            {
                Console.WriteLine("歌曲ID格式错误");
                await ts3Client.SendChannelMessage("歌曲ID格式错误");
                return;
            }

            // 检查歌曲是否存在
            if (musicDetail.songs[0].id != parsedSongId)
            {
                Console.WriteLine("歌曲ID不匹配");
                await ts3Client.SendChannelMessage("歌曲ID不匹配，可能输入了无效的ID");
                return;
            }

            // 检查歌曲是否有播放权限（maxBrLevel不为null通常表示有版权）
            if (musicDetail.privileges != null && musicDetail.privileges.Count > 0 &&
                musicDetail.privileges[0].maxBrLevel == null)
            {
                Console.WriteLine("歌曲无版权或不可播放");
                await ts3Client.SendChannelMessage("该歌曲无版权或不可播放");
                return;
            }

            // 获取歌曲名称和歌手信息用于日志
            string songName = musicDetail.songs[0].name ?? "未知歌曲";
            string artistName = musicDetail.songs[0].ar != null && musicDetail.songs[0].ar.Count > 0
                ? musicDetail.songs[0].ar[0].name
                : "未知歌手";

            Console.WriteLine($"提取的歌曲ID: {parsedSongId}");
            await ts3Client.SendChannelMessage($"提取的歌曲ID: {parsedSongId}");

            // 播放歌曲
            await ProcessSong(parsedSongId, ts3Client, playManager, invoker);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"播放歌曲时出错: {ex.Message}");
            await ts3Client.SendChannelMessage($"播放失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 从用户输入中提取歌曲ID
    /// 支持以下格式：
    /// 1. 纯数字ID：xxxxxxxxx
    /// 2. 完整URL：https://music.163.com/song?id=xxxxxxxxx&userid=xxxxxxxxx
    /// 3. 简化URL：music.163.com/song?id=xxxxxxxxx
    /// 4. 带#的URL：music.163.com/#/song?id=xxxxxxxxx
    /// </summary>
    private string ExtractSongIdFromInput(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        // 如果输入是纯数字，直接返回
        if (long.TryParse(input, out _))
        {
            return input.Trim();
        }

        // 处理URL格式
        try
        {
            Uri uri;
            // 如果输入没有协议头，添加https://
            if (!input.StartsWith("http://") && !input.StartsWith("https://"))
            {
                input = "https://" + input;
            }

            if (Uri.TryCreate(input, UriKind.Absolute, out uri))
            {
                // 解析查询参数
                var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                string id = query["id"];

                if (!string.IsNullOrEmpty(id))
                {
                    // 验证ID是否为数字
                    if (long.TryParse(id, out _))
                    {
                        return id;
                    }
                }

                // 如果没有找到id参数，尝试从路径中提取
                // 例如：music.163.com/song/xxxxxxxxx
                string[] segments = uri.Segments;
                for (int i = 0; i < segments.Length; i++)
                {
                    if (segments[i].Equals("song/", StringComparison.OrdinalIgnoreCase) &&
                        i + 1 < segments.Length)
                    {
                        string potentialId = segments[i + 1].Trim('/');
                        if (long.TryParse(potentialId, out _))
                        {
                            return potentialId;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"提取歌曲ID时出错: {ex.Message}");
        }

        return null;
    }
    //==========================================单曲id播放==========================================

    //===========================================歌单播放===========================================
    [Command("yun gedan")]
    public async Task<string> CommandYunGedan(string arguments, PlayManager playManager, InvokerData invoker, Ts3Client ts3Client, Player player)
    {
        // 初始化设置
        playlist.Clear();
        SetInvoker(invoker);
        SetPlplayManager(playManager);
        SetTs3Client(ts3Client);

        try
        {
            // 获取歌单详情
            string urlSearch = $"{WangYiYunAPI_Address}/playlist/detail?id={arguments}";
            string searchJson = await HttpGetAsync(urlSearch);
            GedanDetail gedanDetail = JsonSerializer.Deserialize<GedanDetail>(searchJson);

            int trackCount = gedanDetail.playlist.trackCount;
            _ = ts3Client.SendChannelMessage($"歌单共{trackCount}首歌曲，正在添加到播放列表,请稍后。");

            // 分批获取歌单中的所有歌曲
            await GetAllSongsFromPlaylist(arguments, trackCount);

            // 开始播放第一首歌
            if (playlist.Count > 0)
            {
                Playlocation = 0;
                _ = ProcessSong(playlist[0], ts3Client, playManager, invoker);

                await Listeninglock.WaitAsync();
                playManager.ResourceStopped += async (sender, e) => await SongPlayMode(playManager, invoker, ts3Client);

                return $"播放列表加载完成,已加载{playlist.Count}首歌";
            }
            else
            {
                return "歌单中没有找到可播放的歌曲";
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"获取歌单时出错: {ex.Message}");
            return $"获取歌单失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 分批获取歌单中的所有歌曲（正确的分页逻辑）
    /// </summary>
    private async Task GetAllSongsFromPlaylist(string playlistId, int totalSongs)
    {
        const int pageSize = 50;
        int page = 0;
        int retrievedSongs = 0;

        while (retrievedSongs < totalSongs)
        {
            Console.WriteLine($"正在获取第 {page + 1} 页歌曲...");

            // 计算偏移量：第0页offset=0，第1页offset=50，第2页offset=100...
            int offset = page * pageSize;
            string url = $"{WangYiYunAPI_Address}/playlist/track/all?id={playlistId}&limit={pageSize}&offset={offset}";
            string json = await HttpGetAsync(url);

            GeDan geDan = JsonSerializer.Deserialize<GeDan>(json);

            if (geDan?.songs == null || geDan.songs.Count == 0)
                break;

            // 添加当前批次的歌曲到播放列表
            int songsInThisBatch = geDan.songs.Count;
            foreach (var song in geDan.songs)
            {
                playlist.Add(song.id);
                Console.WriteLine($"添加歌曲ID: {song.id}");
            }

            retrievedSongs += songsInThisBatch;
            page++;

            Console.WriteLine($"已获取 {retrievedSongs}/{totalSongs} 首歌曲");

            // 如果返回的歌曲数量少于请求的数量，说明已经到达最后一页
            if (songsInThisBatch < pageSize)
                break;
        }

        Console.WriteLine($"歌单获取完成，共{playlist.Count}首歌");
    }
    //===========================================歌单播放===========================================

    //============================================下一曲============================================
    [Command("yun next")]
    public async Task CommandYunNext(PlayManager playManager, InvokerData invoker, Ts3Client ts3Client)
    {
        await SongPlayMode(playManager, invoker, ts3Client);
    }
    //===========================================下一曲=============================================
    [Command("yun stop")]
    public async Task CommandYunStop(PlayManager playManager, Ts3Client ts3Client)
    {
        playlist.Clear();
        await playManager.Stop();
    }
    //============================================歌单jump============================================
    [Command("yun to")]
    public async Task CommandYunTo(string arguments, PlayManager playManager, InvokerData invoker, Ts3Client ts3Client)
    {
        int songto = int.Parse(arguments);
        if (songto <= 0)
        {
            Console.WriteLine("请输入有效的歌曲顺序");
            _ = ts3Client.SendChannelMessage("<=0是想怎滴");
        }
        else if (songto > playlist.Count)
        {
            Console.WriteLine("输入超过列表总数");
            _ = ts3Client.SendChannelMessage($"播放列表里只有{playlist.Count}首歌啦");
        }
        else if (songto <= playlist.Count)
        {
            Playlocation = songto - 1;
            await ProcessSong(playlist[Playlocation], ts3Client, playManager, invoker);
        }
        else
        {
            Console.WriteLine("请输入有效的歌曲顺序");
            _ = ts3Client.SendChannelMessage("请输入有效的歌曲顺序");
        }
    }
    //===========================================歌单jump=============================================

    //===========================================播放逻辑===========================================
    private async Task SongPlayMode(PlayManager playManager, InvokerData invoker, Ts3Client ts3Client)
    {
        try
        {
            if (playlist.Count == 0)
            {
                Console.WriteLine("播放列表为空");
                return;
            }

            switch (playMode)
            {
                case 0: //顺序播放
                    Playlocation += 1;
                    if (Playlocation >= playlist.Count)
                    {
                        Console.WriteLine("顺序播放已到末尾");
                        return;
                    }
                    await ProcessSong(playlist[Playlocation], ts3Client, playManager, invoker);
                    break;
                case 1:  //顺序循环
                    Playlocation = (Playlocation + 1) % playlist.Count;
                    await ProcessSong(playlist[Playlocation], ts3Client, playManager, invoker);
                    break;
                case 2:  //随机播放
                    Random random = new Random();
                    Playlocation = random.Next(0, playlist.Count);
                    await ProcessSong(playlist[Playlocation], ts3Client, playManager, invoker);
                    break;
                case 3:  //随机循环
                    Random random1 = new Random();
                    Playlocation = random1.Next(0, playlist.Count);
                    await ProcessSong(playlist[Playlocation], ts3Client, playManager, invoker);
                    break;
                default:
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"播放出错: {ex.Message}");
            _ = ts3Client.SendChannelMessage("播放出错");
        }
    }
    private async Task ProcessSong(long id, Ts3Client ts3Client, PlayManager playManager, InvokerData invoker)
    {
        await playlock.WaitAsync();
        try
        {
            long musicId = id;
            string musicCheckUrl = $"{WangYiYunAPI_Address}/check/music?id={musicId}";
            string searchMusicCheckJson = await HttpGetAsync(musicCheckUrl);
            MusicCheck musicCheckJson = JsonSerializer.Deserialize<MusicCheck>(searchMusicCheckJson);

            // 根据音乐检查结果获取音乐播放URL
            string musicUrl = musicCheckJson.success.ToString() == "False" ? await GetcheckMusicUrl(musicId, true) : await GetcheckMusicUrl(musicId, true);

            // 构造获取音乐详情的URL
            string musicDetailUrl = $"{WangYiYunAPI_Address}/song/detail?ids={musicId}";
            string musicDetailJson = await HttpGetAsync(musicDetailUrl);
            MusicDetail musicDetail = JsonSerializer.Deserialize<MusicDetail>(musicDetailJson);

            // 从音乐详情中获取音乐图片URL和音乐名称
            string musicImgUrl = musicDetail.songs[0].al.picUrl;
            string musicName = musicDetail.songs[0].name;
            string musicSinger = musicDetail.songs[0].ar[0].name;
            string BotDescription = $"{musicName} - {musicSinger}";
            Console.WriteLine($"歌曲id：{musicId}，歌曲名称：{musicName}，歌手：{musicSinger}，版权：{musicCheckJson.success}");

            // 设置Bot的头像为音乐图片
            _ = MainCommands.CommandBotAvatarSet(ts3Client, musicImgUrl);

            // 设置Bot的描述为音乐名称
            _ = MainCommands.CommandBotDescriptionSet(ts3Client, BotDescription);

            // 在控制台输出音乐播放URL
            Console.WriteLine(musicUrl);

            // 如果音乐播放URL不是错误，则添加到播放列表并通知频道
            if (musicUrl != "error")
            {
                _ = MainCommands.CommandPlay(playManager, invoker, musicUrl);

                // 更新Bot的描述为当前播放的音乐名称
                _ = MainCommands.CommandBotDescriptionSet(ts3Client, BotDescription);

                // 发送消息到频道，通知正在播放的音乐
                if (playlist.Count == 0)
                {
                    _ = ts3Client.SendChannelMessage($"正在播放：{musicName} - {musicSinger}");
                }
                else
                {
                    _ = ts3Client.SendChannelMessage($"正在播放第{Playlocation + 1}首：{musicName} - {musicSinger}");
                }

                // 如果有描述信息，发送描述
                string additionalInfo = "";
                if (musicDetail?.songs?[0]?.tns?.Count > 0 && !string.IsNullOrEmpty(musicDetail.songs[0].tns[0]))
                {
                    additionalInfo += $"{musicDetail.songs[0].tns[0]}";
                }
                if (musicDetail?.songs?[0]?.alia?.Count > 0 && !string.IsNullOrEmpty(musicDetail.songs[0].alia[0]))
                {
                    if (!string.IsNullOrEmpty(additionalInfo)) additionalInfo += " - ";
                    additionalInfo += $"{musicDetail.songs[0].alia[0]}";
                }

                if (!string.IsNullOrEmpty(additionalInfo))
                {
                    additionalInfo = "描述：" + additionalInfo;
                    _ = ts3Client.SendChannelMessage(additionalInfo);
                }
            }
        }
        finally
        {
            playlock.Release();
        }
    }
    //===========================================播放逻辑===========================================

    //===========================================登录部分===========================================
    [Command("yun login")]
    public static async Task<string> CommandLoginAsync(Ts3Client ts3Client, TsFullClient tsClient)
    {
        // 获取登录二维码的 key
        string key = await GetLoginKey();

        // 生成登录二维码并获取二维码图片的 base64 字符串
        string base64String = await GetLoginQRImage(key);

        // 发送二维码图片到 TeamSpeak 服务器频道
        await ts3Client.SendChannelMessage("正在生成二维码");
        await ts3Client.SendChannelMessage(base64String);

        // 将 base64 字符串转换为二进制图片数据，上传到 TeamSpeak 服务器作为头像
        await UploadQRImage(tsClient, base64String);

        // 设置 TeamSpeak 服务器的描述信息
        await ts3Client.ChangeDescription("请用网易云APP扫描二维码登陆");

        int i = 0;
        long code;
        string result;

        while (true)
        {
            // 检查登录状态
            Status1 status = await CheckLoginStatus(key);

            code = status.code;
            cookies = status.cookie;
            i = i + 1;
            Thread.Sleep(1000);

            if (i == 120)
            {
                result = "登陆失败或者超时";
                await ts3Client.SendChannelMessage("登陆失败或者超时");
                break;
            }

            if (code == 803)
            {
                result = "登陆成功";
                await ts3Client.SendChannelMessage("登陆成功");
                break;
            }
        }

        // 登录完成后删除上传的头像
        _ = await tsClient.DeleteAvatar();

        // 更新 cookies 到配置文件
        MyIni.Configs["YunBot"].Set("cookies1", $"\"{cookies}\"");
        MyIni.Save();

        return result;
    }

    // 获取登录二维码的 key
    private static async Task<string> GetLoginKey()
    {
        string url = WangYiYunAPI_Address + "/login/qr/key" + "?timestamp=" + GetTimeStamp();
        string json = await HttpGetAsync(url);
        LoginKey loginKey = JsonSerializer.Deserialize<LoginKey>(json);
        return loginKey.data.unikey;
    }

    // 生成登录二维码并获取二维码图片的 base64 字符串
    private static async Task<string> GetLoginQRImage(string key)
    {
        string url = WangYiYunAPI_Address + $"/login/qr/create?key={key}&qrimg=true&timestamp={GetTimeStamp()}";
        string json = await HttpGetAsync(url);
        LoginImg loginImg = JsonSerializer.Deserialize<LoginImg>(json);
        return loginImg.data.qrimg;
    }

    // 上传二维码图片到 TeamSpeak 服务器
    private static async Task UploadQRImage(TsFullClient tsClient, string base64String)
    {
        string[] img = base64String.Split(",");
        byte[] bytes = Convert.FromBase64String(img[1]);
        Stream stream = new MemoryStream(bytes);
        _ = await tsClient.UploadAvatar(stream);
    }

    // 检查登录状态
    private static async Task<Status1> CheckLoginStatus(string key)
    {
        string url = WangYiYunAPI_Address + $"/login/qr/check?key={key}&timestamp={GetTimeStamp()}";
        string json = await HttpGetAsync(url);
        Status1 status = JsonSerializer.Deserialize<Status1>(json);
        Console.WriteLine(json);
        return status;
    }
    //===============================================登录部分===============================================


    //=============================================获取歌曲信息=============================================
    //以下全是功能性函数
    public static async Task<string> GetMusicUrl(long id, bool usingCookie = false)
    {
        return await GetMusicUrl(id.ToString(), usingCookie);
    }

    public static async Task<string> GetMusicUrl(string id, bool usingCookie = false)
    {
        string url = $"{WangYiYunAPI_Address}/song/url?id={id}";
        if (usingCookie && !string.IsNullOrEmpty(cookies))
        {
            url += $"&cookie={cookies}";
        }

        string musicUrlJson = await HttpGetAsync(url);
        musicURL musicUrl = JsonSerializer.Deserialize<musicURL>(musicUrlJson);

        if (musicUrl.code != 200)
        {
            // 处理错误情况，这里你可以根据实际情况进行适当的处理
            return string.Empty;
        }

        string mp3 = musicUrl.data[0].url;
        return mp3;
    }

    public static async Task<string> GetcheckMusicUrl(long id, bool usingcookie = false) //获得无版权歌曲URL
    {
        string url;
        url = WangYiYunAPI_Address + "/song/url?id=" + id.ToString() + "&proxy=" + UNM_Address;
        string musicurljson = await HttpGetAsync(url);
        musicURL musicurl = JsonSerializer.Deserialize<musicURL>(musicurljson);
        string mp3 = musicurl.data[0].url.ToString();
        string checkmp3 = mp3.Replace("http://music.163.com", UNM_Address);
        return checkmp3;
    }

    public static async Task<string> GetMusicName(string arguments)//获得歌曲名称
    {
        string musicdetailurl = WangYiYunAPI_Address + "/song/detail?ids=" + arguments;
        string musicdetailjson = await HttpGetAsync(musicdetailurl);
        MusicDetail musicDetail = JsonSerializer.Deserialize<MusicDetail>(musicdetailjson);
        string musicname = musicDetail.songs[0].name;
        return musicname;
    }
    //=============================================获取歌曲信息=============================================



    //===============================================HTTP相关===============================================
    public static async Task<string> HttpGetAsync(string url)
    {
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
        request.Method = "GET";
        request.Accept = "text/html, application/xhtml+xml, */*";
        request.ContentType = "application/json";

        // 异步获取响应
        using HttpWebResponse response = (HttpWebResponse)await request.GetResponseAsync();
        // 异步读取响应流
        using StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    public static string GetTimeStamp() //获得时间戳
    {
        TimeSpan ts = DateTime.Now - new DateTime(1970, 1, 1, 0, 0, 0, 0);
        return Convert.ToInt64(ts.TotalSeconds).ToString();
    }
    //===============================================HTTP相关===============================================
    public void Dispose()
    {
        
    }
}
