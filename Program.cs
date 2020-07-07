using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RedditSharp;
using RedditSharp.Things;
using static RedditSharp.Things.ModeratableThing;
using System.IO;
using Newtonsoft.Json;

namespace redditBot
{
    class Program
    {
        static Reddit reddit;        
        private DateTime applicationStart;

        private static Dictionary<string, string> Config;
        private static Dictionary<string, int> FlairConfig;

        private static Subreddit subreddit;
        private static Dictionary<string, Comment> topLevelSummaries = new Dictionary<string, Comment>();
        private static Dictionary<string, NewCommentLock> lockKeyDistributor = new Dictionary<string, NewCommentLock>();

        static void Main(string[] args)

        {
            //Console.WriteLine("Hello World!");
            new Program().test().GetAwaiter().GetResult();
        }

        async Task test(){
            applicationStart = DateTime.UtcNow;

            using (StreamReader sr = new StreamReader(new FileStream("data//Config.json", FileMode.Open)))
                Config = JsonConvert.DeserializeObject<Dictionary<string,string>>(sr.ReadToEnd());

            using (StreamReader sr = new StreamReader(new FileStream("data//FlairConfig.json", FileMode.Open)))
                FlairConfig = JsonConvert.DeserializeObject<Dictionary<string,int>>(sr.ReadToEnd());
            
            var webAgent = new BotWebAgent(Config["botAcc"], Config["botPw"], Config["clientId"], Config["clientSecret"], Config["redirectURI"]);
            
            reddit = new Reddit(webAgent, false);
            await reddit.InitOrUpdateUserAsync();
            
            subreddit = await reddit.GetSubredditAsync(Config["subreddit"]);
            CancellationTokenSource source = new CancellationTokenSource();
            CancellationToken token = source.Token;
            ListingStream<Post> postStream = subreddit.GetPosts(Subreddit.Sort.New).Stream();
            postStream.Subscribe(async post => await newPost(post));
            ListingStream<Comment> commentStream = subreddit.GetComments().Stream();
            commentStream.Subscribe(async comment => await newComment(comment));
            
            ListingStream<VotableThing> removedStream = subreddit.GetRemoved().Stream();
            removedStream.Subscribe(async thing => await removedThing(thing));
            
            await Task.WhenAll(new Task[]{
                    postStream.Enumerate(token),
                    commentStream.Enumerate(token),
                    removedStream.Enumerate(token)
            });
            
        }
        async Task newPost(Post post){
            if(post.CreatedUTC < applicationStart) return; //old post
            //Moved to newComment, only send the sticky message if there actually are comments
            //await (await post.CommentAsync(String.Format("Top-level comments made by {0}:", post.AuthorName))).DistinguishAsync(DistinguishType.Moderator);
            if(!(post.LinkFlairText is null) && FlairConfig.ContainsKey(post.LinkFlairText)){
                String userfile = "";
                using (StreamReader sr = new StreamReader(new FileStream($"data//user//{post.AuthorName}.json", FileMode.OpenOrCreate))){
                    userfile = sr.ReadToEnd();
                }
                User user;
                if(userfile.Equals("")){
                    user = new User();
                }else{
                    user = JsonConvert.DeserializeObject<User>(userfile);
                    //Calculate Monday of Week (UTC)
                    var cal = System.Globalization.DateTimeFormatInfo.CurrentInfo.Calendar;
                    var d1 = user.lastEdit.Date.AddDays(-1 * ((int)cal.GetDayOfWeek(user.lastEdit)-1));
                    var d2 = post.CreatedUTC.Date.AddDays(-1 * ((int) cal.GetDayOfWeek(post.CreatedUTC)-1));
                    if(d1 != d2){ //Diffrent Week
                        foreach(var key in user.FlairCount.Keys.ToList()){
                            user.FlairCount[key] = 0;
                        }
                    }
                }
                if(!user.FlairCount.ContainsKey(post.LinkFlairText)){ 
                    user.FlairCount.Add(post.LinkFlairText, 1); //flair not yet accounted for with this User
                }else{
                    user.FlairCount[post.LinkFlairText] += 1;
                    if(user.FlairCount[post.LinkFlairText] > FlairConfig[post.LinkFlairText]){
                        await post.ReportAsync(ReportType.Other, $"post flooding");
                        //await reddit.ComposePrivateMessageAsync($"Automatic Flair Count Detection", $"The User {post.AuthorName} posted {user.FlairCount[post.LinkFlairText]} Posts with flair {post.LinkFlairText} this week, last one: [{post.Title}]({post.Permalink})", Config["subreddit"]);
                    }
                }
                user.lastEdit = post.CreatedUTC;
                using(StreamWriter sw = new StreamWriter(new FileStream($"data//user//{post.AuthorName}.json", FileMode.OpenOrCreate))){
                    sw.Write(JsonConvert.SerializeObject(user));
                }
            }
        }
        object syncThreads = new object();
        async Task newComment(Comment comment){
            if(comment.CreatedUTC < applicationStart) return; //old comment
            //Console.WriteLine($"Post : [{comment.FullName} at {comment.CreatedUTC}]");
            if(comment.AuthorName.Equals(Config["botAcc"]) && comment.Distinguished == DistinguishType.Moderator){
                return; //only relevant if a post by the botAcc happens, (=> testing)
            }

            lock(syncThreads){
                if(!lockKeyDistributor.ContainsKey(comment.ParentId)){
                    lockKeyDistributor[comment.ParentId] = new NewCommentLock(){id = comment.ParentId};
                }
            }

            Thing parent = comment.Parent ?? await reddit.GetThingByFullnameAsync(comment.ParentId);

            if(parent is Post && (parent as Post).AuthorName.Equals(comment.AuthorName)){ //Comment is Toplevel and by the Post Auther
                string commentline = comment.Body.Split("  \n")[0].Split("\n\n")[0];
                string commentSummary = commentline.Length > 50 ? commentline.Substring(0, 50)+"..." : commentline;
                lock(lockKeyDistributor[comment.ParentId]){
                    if(!topLevelSummaries.ContainsKey(comment.ParentId)){ //comment not in memory
                        Comment botComment = (parent as Post).GetCommentsAsync().Result.FirstOrDefault(c => c.AuthorName.Equals(Config["botAcc"]) && c.Distinguished == DistinguishType.Moderator && c.IsStickied);
                        if(botComment is null){ //send the Sticky Message
                            topLevelSummaries[comment.ParentId] = ((parent as Post).CommentAsync(String.Format($"{(parent as Post).AuthorName} has made the following comment(s) regarding their post:   \n[{commentSummary}]({comment.Permalink})")).Result);
                            topLevelSummaries[comment.ParentId].DistinguishAsync(DistinguishType.Moderator, true).Wait();
                        }else{ // append to the Sticky Message
                            botComment.EditTextAsync(botComment.Body + String.Format($"  \n[{commentSummary}]({comment.Permalink})")).Wait();
                            topLevelSummaries[comment.ParentId] = botComment;
                        }  
                    }else{ // append to the Sticky Message
                        Comment botComment = topLevelSummaries[comment.ParentId];
                        botComment.EditTextAsync(botComment.Body + String.Format($"  \n[{commentSummary}]({comment.Permalink})")).Wait();
                    }
                }
            }
        }

        async Task removedThing(VotableThing thing){
            if(thing.FetchedAt.ToUniversalTime() < applicationStart.AddSeconds(5)) return;
            if(thing is Post){
                Post post = thing as Post; 
                if(!(post.LinkFlairText is null) && FlairConfig.ContainsKey(post.LinkFlairText)){
                    String userfile = "";
                    using (StreamReader sr = new StreamReader(new FileStream($"data//user//{post.AuthorName}.json", FileMode.OpenOrCreate))){
                        userfile = sr.ReadToEnd();
                    }
                    User user;
                    if(userfile.Equals("")){
                        user = new User();
                    }else{
                        user = JsonConvert.DeserializeObject<User>(userfile);
                        //Calculate Monday of Week (UTC)
                        var cal = System.Globalization.DateTimeFormatInfo.CurrentInfo.Calendar;
                        var d1 = user.lastEdit.Date.AddDays(-1 * ((int)cal.GetDayOfWeek(user.lastEdit)-1));
                        var d2 = post.CreatedUTC.Date.AddDays(-1 * ((int) cal.GetDayOfWeek(post.CreatedUTC)-1));
                        if(d1 != d2){ //Diffrent Week
                            return;
                        }
                    }
                    if(!user.FlairCount.ContainsKey(post.LinkFlairText)){
                        user.FlairCount.Add(post.LinkFlairText, 0);
                        return; //flair not yet accounted for with this User
                    }else{
                        user.FlairCount[post.LinkFlairText] -= 1;
                        if(user.FlairCount[post.LinkFlairText] < 0){ //wtf?
                            user.FlairCount[post.LinkFlairText] = 0;
                        }
                    }
                    using(StreamWriter sw = new StreamWriter(new FileStream($"data//user//{post.AuthorName}.json", FileMode.OpenOrCreate))){
                        sw.Write(JsonConvert.SerializeObject(user));
                    }
                }
            }
        }

    }

    class User{
        public Dictionary<string, int> FlairCount;
        public DateTime lastEdit;

        public User(){
            FlairCount = new Dictionary<string, int>();
            lastEdit = DateTime.MinValue;
        }
    }

    class NewCommentLock{
        public string id;
    }
}
