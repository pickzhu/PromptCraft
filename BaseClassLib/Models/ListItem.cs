namespace BaseClassLib.Models
{
    public class ListItem<T>
    {
        public ListItem(T value, string text, int sort)
        {
            Value = value;
            Text = text;
            Sort = sort;
        }
        public T? Value { get; set; }
        public string? Text { get; set; }
        public int Sort { get; set; }

        public override string? ToString()
        {
            if (!string.IsNullOrEmpty(Text))
                return Text;
            if (Value != null)
                return Value.ToString();
            return base.ToString();
        }
    }
}
