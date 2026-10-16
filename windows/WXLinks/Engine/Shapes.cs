namespace WXLinks.Engine;

/// 窗口内坐标的矩形（像素）
public record struct Box(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
    public double MidX => X + W / 2;
    public double MidY => Y + H / 2;
}

/// 认出来的一段字，坐标是窗口内像素
public record TextBox(string Text, Box Rect);

