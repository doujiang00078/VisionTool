using System.Collections.Generic;

namespace VisionTool
{
    public class MedianCalculator
    {
        private const int MaxCapacity = 400;

        /// <summary>
        /// 循环缓冲区：存储最近的 400 个数据
        /// </summary>
        private readonly double[] _buffer = new double[MaxCapacity];

        /// <summary>
        /// 有序列表：用于快速计算中位数
        /// </summary>
        private readonly List<double> _sortedList = new(MaxCapacity);

        /// <summary>
        /// 当前写入位置（循环索引）
        /// </summary>
        private int _writeIndex = 0;

        /// <summary>
        /// 当前实际数据量
        /// </summary>
        private int _count = 0;

        /// <summary>
        /// 添加一个数字（支持小数）
        /// 维持最近 400 个数据，超过后从头替换
        /// </summary>
        /// <param name="num">要添加的数字</param>
        public void AddNum(double num)
        {
            // 如果达到容量上限，需要移除最旧的数据
            if (_count == MaxCapacity)
            {
                double oldValue = _buffer[_writeIndex];
                _sortedList.Remove(oldValue); // 从有序列表中移除旧值
            }
            else
            {
                _count++;
            }

            // 写入新数据到循环缓冲区
            _buffer[_writeIndex] = num;
            _writeIndex = (_writeIndex + 1) % MaxCapacity;

            // 将新数据插入到有序列表中
            int index = _sortedList.BinarySearch(num);
            if (index < 0)
            {
                index = ~index;
            }
            _sortedList.Insert(index, num);
        }

        /// <summary>
        /// 获取当前列表的中位数
        /// </summary>
        public double GetMedian()
        {
            if (_count == 0)
                return 0d;

            int mid = _count / 2;

            if (_count % 2 != 0)
            {
                // 奇数个元素，取正中间那个
                return _sortedList[mid];
            }
            else
            {
                // 偶数个元素，取中间两个数的平均值
                return (_sortedList[mid - 1] + _sortedList[mid]) / 2.0;
            }
        }

        /// <summary>
        /// 清空数据
        /// </summary>
        public void Clear()
        {
            _sortedList.Clear();
            _writeIndex = 0;
            _count = 0;
        }

        /// <summary>
        /// 获取当前数据总量
        /// </summary>
        public int Count => _count;

        /// <summary>
        /// 获取最大容量
        /// </summary>
        public int MaxCount => MaxCapacity;
    }
}
