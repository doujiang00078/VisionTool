using Newtonsoft.Json.Linq;

namespace VisionTool
{
    public abstract class BaseTool
    {
        public int Id()
        { return mId; }

        public void SetId(int val)
        {
            if (mId != val)
                mId = val;
        }

        public string Name()
        { return mName; }

        public string Comment()
        { return mComment; }

        public string Icon()
        { return mIcon; }

        public string Tips()
        { return mTips; }

        public string Description()
        { return mDescription; }

        /// <summary>
        /// 工具名称
        /// </summary>
        public string UserName
        {
            get { return mUserName; }
            set
            {
                if (value != mUserName)
                    mUserName = value;
            }
        }

        /// <summary>
        /// 获取文件目录
        /// </summary>
        /// <returns></returns>
        public string FileStorageDirectory()
        {
            return mFileDirectory;
        }

        /// <summary>
        /// 设置文件目录
        /// </summary>
        /// <param name="directory"></param>
        public void SetFileStorageDirectory(string directory)
        {
            mFileDirectory = directory;
        }

        /// <summary>
        /// 备份
        /// </summary>
        public abstract void Backup();

        public abstract void RollBack();

        /// <summary>
        /// 加载参数
        /// </summary>
        /// <param name="json"></param>
        public abstract void Load(JObject json);

        /// <summary>
        /// 保存参数
        /// </summary>
        /// <param name="json"></param>
        public abstract void Save(JObject json);

        protected void SetName(string name)
        {
            mName = name;
        }

        protected void SetComment(string comment)
        {
            mComment = comment;
        }

        protected void SetIcon(string icon)
        {
            mIcon = icon;
        }

        protected void SetDescription(string description)
        {
            mDescription = description;
        }

        public virtual void Test(ref JObject json)
        {
        }

        protected int mId;
        protected string mName;
        protected string mUserName;
        protected string mComment;
        protected string mIcon;
        protected string mTips;
        protected string mDescription;
        protected string mFileDirectory;
        protected string mUserNameBackup;
    }
}