﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web;
using System.Web.Hosting;
using System.Web.Http;
using System.Web.Routing;
using mobSocial.Data.Constants;
using mobSocial.Data.Entity.MediaEntities;
using mobSocial.Data.Entity.Settings;
using mobSocial.Data.Entity.Timeline;
using mobSocial.Data.Entity.Users;
using mobSocial.Data.Helpers;
using mobSocial.Services.Battles;
using mobSocial.Services.Extensions;
using mobSocial.Services.Helpers;
using mobSocial.Services.MediaServices;
using mobSocial.Services.Social;
using mobSocial.Services.Timeline;
using mobSocial.Services.Users;
using mobSocial.WebApi.Configuration.Infrastructure;
using mobSocial.WebApi.Configuration.Mvc;
using mobSocial.WebApi.Models.Timeline;
using Newtonsoft.Json;
using NReco.VideoConverter;

namespace mobSocial.WebApi.Controllers
{
    [RoutePrefix("timelines")]
    public class TimelineController : RootApiController
    {
        private readonly ITimelineService _timelineService;
        private readonly IUserService _userService;
        private readonly IFollowService _customerFollowService;
        private readonly ILikeService _customerLikeService;
        private readonly ICommentService _customerCommentService;
        private readonly IMediaService _pictureService;
        private readonly IVideoBattleService _videoBattleService;
        private readonly ITimelinePostProcessor _timelinePostProcessor;
        private readonly MediaSettings _mediaSettings;
        
        public TimelineController(ITimelineService timelineService,
            IFollowService customerFollowService,
            IUserService userService,
            IMediaService pictureService,
            MediaSettings mediaSettings,
            IVideoBattleService videoBattleService, 
            ILikeService customerLikeService, 
            ICommentService customerCommentService, ITimelinePostProcessor timelinePostProcessor)
        {
            _timelineService = timelineService;
            _customerFollowService = customerFollowService;
            _userService = userService;
            _pictureService = pictureService;
            _mediaSettings = mediaSettings;
            _videoBattleService = videoBattleService;
            _customerLikeService = customerLikeService;
            _customerCommentService = customerCommentService;
            _timelinePostProcessor = timelinePostProcessor;
        }

        [Route("post")]
        [HttpPost]
        [Authorize]
        public IHttpActionResult Post(TimelinePostModel model)
        {
            if (!ModelState.IsValid)
                return Response(new { Success = false, Message = "Invalid data" });

            //TODO: check OwnerId for valid values and store entity name accordingly, these can be customer, artist page, videobattle page etc.
            model.OwnerId = ApplicationContext.Current.CurrentUser.Id;
            model.OwnerEntityType = TimelinePostOwnerTypeNames.Customer;
            if(model.PublishDate < DateTime.UtcNow)
                model.PublishDate = DateTime.UtcNow;

            //create new timeline post
            var post = new TimelinePost() {
                Message = model.Message,
                AdditionalAttributeValue = model.AdditionalAttributeValue,
                PostTypeName = model.PostTypeName,
                DateCreated = DateTime.UtcNow,
                DateUpdated = DateTime.UtcNow,
                OwnerId = model.OwnerId,
                IsSponsored = model.IsSponsored,
                OwnerEntityType = model.OwnerEntityType,
                LinkedToEntityName = model.LinkedToEntityName,
                LinkedToEntityId = model.LinkedToEntityId,
                PublishDate = model.PublishDate,
                InlineTags = model.InlineTags != null ? JsonConvert.SerializeObject(model.InlineTags) : null
            };

            //save it
            _timelineService.Insert(post);

            var postModel = PrepareTimelinePostDisplayModel(post);

            return Response(new { Success = true, Post = postModel });
        }

        [Route("get")]
        [HttpGet]
        public IHttpActionResult Get([FromUri] TimelinePostsRequestModel model)
        {
            var customerId = model.CustomerId;
            var page = model.Page;
            //we need to get posts from followers, friends, self, and anything else that supports posting to timeline

            //but we need to know if the current customer is a registered user or a guest
            var isRegistered = ApplicationContext.Current.CurrentUser.IsRegistered();
            //the posts that'll be returned
            List<TimelinePost> timelinePosts = null;

            //the number of posts
            var count = model.Count;
            //if the user is registered, then depending on value of customerId, we fetch posts. 
            //{if the customer id matches the logged in user id, he is seeing his own profile, so we show the posts by her only
            //{if the customer id is zero, then we show posts by her + posts by her friends + posts by people she is following etc.
            //{if the customer id is non-zero, then we show posts by the customer of customerId
            if (isRegistered)
            {
                if (customerId != 0)
                {
                    //we need to get posts by this customer
                    timelinePosts = _timelineService.GetByEntityIds("customer", new[] { customerId }, true, count, page).ToList();
                }
                else
                {
                    customerId = ApplicationContext.Current.CurrentUser.Id;
                    //we need to find he person she is following.
                    var allFollows = _customerFollowService.GetFollowing<User>(customerId);

                    //get all the customer's ids which she is following
                    var customerIds =
                        allFollows.Where(x => x.FollowingEntityName == typeof(User).Name)
                            .Select(x => x.FollowingEntityId).ToList();

                    //and add current customer has well to cover her own posts
                    customerIds.Add(ApplicationContext.Current.CurrentUser.Id);


                    //get timeline posts
                    timelinePosts = _timelineService.GetByEntityIds("customer", customerIds.ToArray(), true, count, page).ToList();

                }
            }
            else
            {
                //should we show the data to non logged in user?
                //return null;
                timelinePosts = _timelineService.GetByEntityIds("customer", new[] { customerId }, true, count, page).ToList();
            }

            var responseModel = new List<TimelinePostDisplayModel>();

            foreach (var post in timelinePosts)
            {
                var postModel = PrepareTimelinePostDisplayModel(post);
                responseModel.Add(postModel);
            }

            return Response(responseModel);
        }

        [Authorize]
        [Route("delete/{timelinePostId:int}")]
        [HttpDelete]
        public IHttpActionResult Delete(int timelinePostId)
        {
            //first get the timeline post
            var post = _timelineService.Get(timelinePostId);

            if (post == null)
                return Response(new { Success = false, Message = "Post doesn't exist" });

            //only admin or post owner should be able to delete the post
            if (post.OwnerId == ApplicationContext.Current.CurrentUser.Id || ApplicationContext.Current.CurrentUser.IsAdministrator())
            {
                _timelineService.Delete(post);

                return Response(new { Success = true });
            }
            return Response(new { Success = false, Message = "Unauthorized" });
        }

        [Authorize]
        [Route("uploadpictures")]
        [HttpPost]
        public IHttpActionResult UploadPictures()
        {
            var files = HttpContext.Current.Request.Files;
            if (files.Count == 0)
                return Response(new { Success = false, Message = "No file uploaded" });

            var newImages = new List<object>();
            for (var index = 0; index < files.Count; index++)
            {

                //the file
                var file = files[index];

                //and it's name
                var fileName = file.FileName;
                //stream to read the bytes
                var stream = file.InputStream;
                var pictureBytes = new byte[stream.Length];
                stream.Read(pictureBytes, 0, pictureBytes.Length);

                //file extension and it's type
                var fileExtension = Path.GetExtension(fileName);
                if (!string.IsNullOrEmpty(fileExtension))
                    fileExtension = fileExtension.ToLowerInvariant();

                var contentType = file.ContentType;

                if (string.IsNullOrEmpty(contentType))
                {
                    contentType = PictureUtility.GetContentType(fileExtension);
                }

                var picture = new Media()
                {
                    Binary = pictureBytes,
                    MimeType = contentType,
                    Name = fileName
                };

                _pictureService.WritePictureBytes(picture, _mediaSettings.PictureSaveLocation);
                
                newImages.Add(new {
                    ImageUrl = _pictureService.GetPictureUrl(picture.Id),
                    SmallImageUrl = _pictureService.GetPictureUrl(picture.Id),
                    ImageId = picture.Id,
                    MimeType = contentType
                });
            }

            return Json(new { Success = true, Images = newImages });
        }

        [Authorize]
        [Route("uploadvideo")]
        [HttpPost]
        public IHttpActionResult UploadVideo()
        {
            var files = HttpContext.Current.Request.Files;
            if (files.Count == 0)
                return Response(new { Success = false, Message = "No file uploaded" });

            var file = files[0];
            //and it's name
            var fileName = file.FileName;


            //file extension and it's type
            var fileExtension = Path.GetExtension(fileName);
            if (!string.IsNullOrEmpty(fileExtension))
                fileExtension = fileExtension.ToLowerInvariant();

            var contentType = file.ContentType;

            if (string.IsNullOrEmpty(contentType))
            {
                contentType = VideoUtility.GetContentType(fileExtension);
            }

            if (contentType == string.Empty)
            {
                return Response(new { Success = false, Message = "Invalid file type" });
            }

            var tickString = DateTime.Now.Ticks.ToString();
            var savePath = Path.Combine(_mediaSettings.VideoSavePath, tickString + fileExtension);
            file.SaveAs(HostingEnvironment.MapPath(savePath));
            //TODO: Create a standard controller for handling uploads
            //wanna generate the thumbnails for videos...ffmpeg is our friend
            var ffmpeg = new FFMpegConverter();
            var thumbnailFilePath = Path.Combine(_mediaSettings.PictureSavePath) + tickString + ".thumb.jpg";
            ffmpeg.GetVideoThumbnail(HostingEnvironment.MapPath(savePath), HostingEnvironment.MapPath(thumbnailFilePath));
            //save the picture now

            return Json(new {
                Success = true,
                VideoUrl = savePath.Replace("~", ""),
                ThumbnailUrl = thumbnailFilePath.Replace("~", ""),
                MimeType = file.ContentType
            });
        }

        #region Helpers

        private TimelinePostDisplayModel PrepareTimelinePostDisplayModel(TimelinePost post)
        {
            //total likes for this post
            var totalLikes = _customerLikeService.GetLikeCount<TimelinePost>(post.Id);
            //the like status for current customer
            var likeStatus =
                _customerLikeService.GetCustomerLike<TimelinePost>(ApplicationContext.Current.CurrentUser.Id, post.Id) == null
                    ? 0
                    : 1;

            //process post content to replace inline tags
            _timelinePostProcessor.ProcessInlineTags(post);

            var totalComments = _customerCommentService.GetCommentsCount(post.Id, typeof (TimelinePost).Name);
            var postModel = new TimelinePostDisplayModel() {
                Id = post.Id,
                DateCreatedUtc = post.DateCreated,
                DateUpdatedUtc = post.DateUpdated,
                DateCreated = DateTimeHelper.GetDateInUserTimeZone(post.DateCreated, DateTimeKind.Utc, ApplicationContext.Current.CurrentUser),
                DateUpdated = DateTimeHelper.GetDateInUserTimeZone(post.DateUpdated, DateTimeKind.Utc, ApplicationContext.Current.CurrentUser),
                OwnerId = post.OwnerId,
                OwnerEntityType = post.OwnerEntityType,
                PostTypeName = post.PostTypeName,
                IsSponsored = post.IsSponsored,
                Message = post.Message,
                AdditionalAttributeValue = post.AdditionalAttributeValue,
                CanDelete = post.OwnerId == ApplicationContext.Current.CurrentUser.Id || ApplicationContext.Current.CurrentUser.IsAdministrator(),
                TotalLikes = totalLikes,
                LikeStatus = likeStatus,
                TotalComments = totalComments
            };
            if (post.OwnerEntityType == TimelinePostOwnerTypeNames.Customer)
            {
                //get the customer to retrieve info such a profile image, profile url etc.
                var user = _userService.Get(post.OwnerId);

                postModel.OwnerName = string.IsNullOrEmpty(user.Name) ? user.Email : user.Name;
                postModel.OwnerImageUrl = _pictureService.GetPictureUrl(user.GetPropertyValueAs<int>(PropertyNames.DefaultPictureId), PictureSizeNames.MediumProfileImage);
                if (string.IsNullOrEmpty(postModel.OwnerImageUrl))
                    postModel.OwnerImageUrl = _mediaSettings.DefaultUserProfileImageUrl;
            }
            //depending on the posttype, we may need to extract additional data e.g. in case of autopublished posts, we may need to query the linked entity
            switch (post.PostTypeName)
            {
                case TimelineAutoPostTypeNames.VideoBattle.Publish:
                case TimelineAutoPostTypeNames.VideoBattle.BattleStart:
                case TimelineAutoPostTypeNames.VideoBattle.BattleComplete:
                    //we need to query the video battle
                    if (post.LinkedToEntityId != 0)
                    {
                        var battle = _videoBattleService.Get(post.LinkedToEntityId);
                        if (battle == null)
                            break;
                        var battleUrl = Url.Route("VideoBattlePage",
                            new RouteValueDictionary()
                                {
                                    {"SeName", battle.GetPermalink()}
                                });

                        //create a dynamic object for battle, we'll serialize this object to json and store as additional attribute value
                        //todo: to see if we have some better way of doing this
                        var coverImageUrl = "";
                        var coverImageId = battle.GetPropertyValueAs<int>(PropertyNames.DefaultCoverId);
                        if (coverImageId != 0)
                            coverImageUrl = _pictureService.GetPictureUrl(coverImageId);
                        var obj = new {
                            Name = battle.Name,
                            Url = battleUrl,
                            Description = battle.Description,
                            VotingStartDate = battle.VotingStartDate,
                            VotingEndDate = battle.VotingEndDate,
                            CoverImageUrl = coverImageUrl,
                            RemainingSeconds = battle.GetRemainingSeconds(),
                            Status = battle.VideoBattleStatus.ToString()
                        };

                        postModel.AdditionalAttributeValue = JsonConvert.SerializeObject(obj);

                    }
                    break;

            }

            //replace inline tags with html links

            return postModel;
        }
        #endregion
    }
}
