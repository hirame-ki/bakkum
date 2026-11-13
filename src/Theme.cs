// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 바꿈 디자인(DESIGN.md '색종이'): 아홉 가지 색, 둥근 모서리, 부드러운 그림자, 톡 튀는 움직임.
// 글꼴은 Pretendard가 깔려 있으면 쓰고, 없으면 맑은 고딕.
// 색·모서리·그림자는 설정에서 고른 스타일·색감(Look.cs)을 켤 때 한 번 읽는다. 기본은 색종이 그대로다.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using ShapePath = System.Windows.Shapes.Path;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace Bakkum
{
    /// 한 가지 색의 세 단계. 쉬는 칸은 옅은 칠(Tint), 마우스가 올라가면 진한 칠(Vivid),
    /// 옅은 칠 위의 글자·그림은 깊은 색(Deep). 진한 칠 위 글자는 늘 잉크색.
    public class Hue
    {
        readonly string name;
        readonly Color vivid, tint, deep;
        readonly Brush vividBrush, tintBrush, deepBrush;

        public Hue(string name, uint vivid, uint tint, uint deep) : this(name, Theme.Hex(vivid), Theme.Hex(tint), Theme.Hex(deep))
        {
        }

        public Hue(string name, Color vivid, Color tint, Color deep)
        {
            this.name = name;
            this.vivid = vivid;
            this.tint = tint;
            this.deep = deep;
            vividBrush = Theme.Frozen(this.vivid);
            tintBrush = Theme.Frozen(this.tint);
            deepBrush = Theme.Frozen(this.deep);
        }

        public string Name { get { return name; } }
        public Color Vivid { get { return vivid; } }
        public Color Tint { get { return tint; } }
        public Color Deep { get { return deep; } }
        public Brush VividBrush { get { return vividBrush; } }
        public Brush TintBrush { get { return tintBrush; } }
        public Brush DeepBrush { get { return deepBrush; } }
    }

    public static class Theme
    {
        // ── 바탕색 ──
        // 색종이 기본값: 책상 F6F3EE · 흰 면 FFFFFF · 옅은 면 FAF7F2 · 잉크 22252F/4B5160/696F7D · 선 ECE7DF/DDD7CD · 포커스 3D7BFF · 흐린 칸 EFECE6
        public static readonly Color DeskColor = Look.Current.Desk;
        public static readonly Color SheetColor = Look.Current.Sheet;
        public static readonly Color Sheet2Color = Look.Current.Sheet2;
        public static readonly Color InkColor = Look.Current.Ink;
        public static readonly Color Ink2Color = Look.Current.Ink2;
        public static readonly Color Ink3Color = Look.Current.Ink3;
        public static readonly Color LineColor = Look.Current.Line;
        public static readonly Color Line2Color = Look.Current.Line2;
        public static readonly Color FocusColor = Look.Current.Focus;
        public static readonly Color MutedFillColor = Look.Current.Muted;

        public static readonly Brush Desk = Look.DeskBrush();
        public static readonly Brush Sheet = Frozen(SheetColor);
        public static readonly Brush Sheet2 = Frozen(Sheet2Color);
        public static readonly Brush Ink = Frozen(InkColor);
        public static readonly Brush Ink2 = Frozen(Ink2Color);
        public static readonly Brush Ink3 = Frozen(Ink3Color);
        public static readonly Brush Line = Frozen(LineColor);
        public static readonly Brush Line2 = Frozen(Line2Color);
        public static readonly Brush Focus = Frozen(FocusColor);

        // ── 색종이 아홉 장 ──
        public static readonly Hue Coral = Look.HueFor(Look.Current, "coral", 0xFF8473, 0xFFE8E3, 0xB53A28);
        public static readonly Hue Orange = Look.HueFor(Look.Current, "orange", 0xFFAA4D, 0xFFEEDB, 0xA85A0B);
        public static readonly Hue Yellow = Look.HueFor(Look.Current, "yellow", 0xFFD447, 0xFFF6D6, 0x8A6A00);
        public static readonly Hue Green = Look.HueFor(Look.Current, "green", 0x52D3A0, 0xDDF7EC, 0x13795A);
        public static readonly Hue Blue = Look.HueFor(Look.Current, "blue", 0x6CA8FF, 0xE3EEFF, 0x2259B8);
        public static readonly Hue Purple = Look.HueFor(Look.Current, "purple", 0xB596FF, 0xEFE8FF, 0x6237C2);
        public static readonly Hue Pink = Look.HueFor(Look.Current, "pink", 0xFF8FC2, 0xFFE6F1, 0xB02A6B);
        public static readonly Hue Slate = Look.HueFor(Look.Current, "slate", 0xAEB6C5, 0xEEF0F4, 0x465063);
        public static readonly Hue Sky = Look.HueFor(Look.Current, "sky", 0x4FC3E8, 0xDFF5FC, 0x0E6F8C);
        public static readonly Hue Lime = Look.HueFor(Look.Current, "lime", 0xA5D64C, 0xF0F9DF, 0x4E7A0C);
        public static readonly Hue Teal = Look.HueFor(Look.Current, "teal", 0x38C7B8, 0xDFF7F4, 0x0D6F66);
        public static readonly Hue Indigo = Look.HueFor(Look.Current, "indigo", 0x8E9BFF, 0xE9EBFF, 0x3B4BC0);

        public static readonly Hue[] Rainbow = { Coral, Orange, Yellow, Green, Blue, Purple, Pink };

        /// 메뉴 칸마다 정해진 색. 어디서든 같은 할 일은 같은 색이다.
        public static Hue HueOf(string actionId)
        {
            switch (actionId)
            {
                case "pdf": return Coral;
                case "image": return Blue;
                case "format": return Purple;
                case "shrink": return Green;
                case "text": return Yellow;
                case "photo": return Orange;
                case "pdftool": return Pink;
                case "ai": return Sky;
                case "archive": return Lime;
                case "media": return Teal;
                case "record": return Indigo;
            }
            return Slate;
        }

        public static readonly FontFamily Font = new FontFamily("Pretendard, Malgun Gothic");
        public static readonly FontFamily MonoFont = new FontFamily("Consolas, Malgun Gothic");

        public static Color Hex(uint rgb)
        {
            return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }

        public static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        public static Color WithAlpha(Color c, byte a)
        {
            return Color.FromArgb(a, c.R, c.G, c.B);
        }

        /// 그림자(값은 색종이 기준, 고른 스타일에 맞게 Look이 바꾼다)
        public static DropShadowEffect Shadow(Color color, double blur, double depth, double opacity)
        {
            return Look.Shadow(color, blur, depth, opacity);
        }

        internal const string Xaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <FontFamily x:Key='UiFont'>Pretendard, Malgun Gothic</FontFamily>

  <ControlTemplate x:Key='FlatButton' TargetType='Button'>
    <Grid x:Name='Root' RenderTransformOrigin='0.5,0.5'>
      <Border x:Name='Ring' BorderBrush='#3d7bff' BorderThickness='2' CornerRadius='16' Margin='-4' Visibility='Collapsed'/>
      <Border x:Name='Bd' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}'
              BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='12' Padding='{TemplateBinding Padding}'>
        <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' RecognizesAccessKey='False'/>
      </Border>
    </Grid>
    <ControlTemplate.Triggers>
      <Trigger Property='IsKeyboardFocused' Value='True'>
        <Setter TargetName='Ring' Property='Visibility' Value='Visible'/>
      </Trigger>
      <Trigger Property='IsPressed' Value='True'>
        <Setter TargetName='Root' Property='RenderTransform'>
          <Setter.Value><ScaleTransform ScaleX='0.97' ScaleY='0.97'/></Setter.Value>
        </Setter>
      </Trigger>
      <Trigger Property='IsEnabled' Value='False'>
        <Setter Property='Opacity' Value='0.45'/>
      </Trigger>
    </ControlTemplate.Triggers>
  </ControlTemplate>

  <ControlTemplate x:Key='RaisedButton' TargetType='Button'>
    <Grid x:Name='Root' RenderTransformOrigin='0.5,0.5'>
      <Border x:Name='Ring' BorderBrush='#3d7bff' BorderThickness='2' CornerRadius='16' Margin='-4' Visibility='Collapsed'/>
      <Border x:Name='Shade' CornerRadius='12' Background='{TemplateBinding Background}'>
        <Border.Effect>
          <DropShadowEffect Color='#22252f' BlurRadius='14' ShadowDepth='4' Direction='270' Opacity='0.24'/>
        </Border.Effect>
      </Border>
      <Border x:Name='Bd' Background='{TemplateBinding Background}' CornerRadius='12' Padding='{TemplateBinding Padding}'>
        <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' RecognizesAccessKey='False'/>
      </Border>
    </Grid>
    <ControlTemplate.Triggers>
      <Trigger Property='IsKeyboardFocused' Value='True'>
        <Setter TargetName='Ring' Property='Visibility' Value='Visible'/>
      </Trigger>
      <Trigger Property='IsPressed' Value='True'>
        <Setter TargetName='Root' Property='RenderTransform'>
          <Setter.Value><ScaleTransform ScaleX='0.97' ScaleY='0.97'/></Setter.Value>
        </Setter>
        <Setter TargetName='Shade' Property='Opacity' Value='0.5'/>
      </Trigger>
      <Trigger Property='IsEnabled' Value='False'>
        <Setter Property='Opacity' Value='0.45'/>
        <Setter TargetName='Shade' Property='Visibility' Value='Hidden'/>
      </Trigger>
    </ControlTemplate.Triggers>
  </ControlTemplate>

  <Style x:Key='BtnBase' TargetType='Button'>
    <Setter Property='FontFamily' Value='{StaticResource UiFont}'/>
    <Setter Property='FontSize' Value='14.5'/>
    <Setter Property='FontWeight' Value='Bold'/>
    <Setter Property='Height' Value='44'/>
    <Setter Property='Padding' Value='20,0'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template' Value='{StaticResource FlatButton}'/>
  </Style>
  <Style x:Key='Primary' TargetType='Button' BasedOn='{StaticResource BtnBase}'>
    <Setter Property='Background' Value='#22252f'/>
    <Setter Property='Foreground' Value='#ffffff'/>
    <Setter Property='BorderThickness' Value='0'/>
    <Setter Property='Template' Value='{StaticResource RaisedButton}'/>
    <Style.Triggers>
      <Trigger Property='IsMouseOver' Value='True'>
        <Setter Property='Background' Value='#3a3e4c'/>
      </Trigger>
    </Style.Triggers>
  </Style>
  <Style x:Key='Line' TargetType='Button' BasedOn='{StaticResource BtnBase}'>
    <Setter Property='Background' Value='#ffffff'/>
    <Setter Property='Foreground' Value='#22252f'/>
    <Setter Property='BorderBrush' Value='#ddd7cd'/>
    <Setter Property='BorderThickness' Value='1.5'/>
    <Style.Triggers>
      <Trigger Property='IsMouseOver' Value='True'>
        <Setter Property='Background' Value='#faf7f2'/>
        <Setter Property='BorderBrush' Value='#bfb8ac'/>
      </Trigger>
    </Style.Triggers>
  </Style>
  <Style x:Key='Quiet' TargetType='Button' BasedOn='{StaticResource BtnBase}'>
    <Setter Property='Background' Value='Transparent'/>
    <Setter Property='Foreground' Value='#4b5160'/>
    <Setter Property='BorderThickness' Value='0'/>
    <Setter Property='Height' Value='38'/>
    <Setter Property='FontSize' Value='14'/>
    <Setter Property='Padding' Value='12,0'/>
    <Style.Triggers>
      <Trigger Property='IsMouseOver' Value='True'>
        <Setter Property='Background' Value='#efeae2'/>
        <Setter Property='Foreground' Value='#22252f'/>
      </Trigger>
    </Style.Triggers>
  </Style>

  <Style x:Key='Chip' TargetType='RadioButton'>
    <Setter Property='FontFamily' Value='{StaticResource UiFont}'/>
    <Setter Property='FontSize' Value='14.5'/>
    <Setter Property='FontWeight' Value='Bold'/>
    <Setter Property='Foreground' Value='#4b5160'/>
    <Setter Property='Background' Value='#ffffff'/>
    <Setter Property='BorderBrush' Value='#e3ddd3'/>
    <Setter Property='MinHeight' Value='46'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='RadioButton'>
          <Grid>
            <Border x:Name='Ring' BorderBrush='#3d7bff' BorderThickness='2' CornerRadius='17' Margin='-4' Visibility='Collapsed'/>
            <Border x:Name='Bd' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}'
                    BorderThickness='1.5' CornerRadius='13' Padding='12,8'>
              <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' RecognizesAccessKey='False'/>
            </Border>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='Bd' Property='Background' Value='{Binding Tag.TintBrush, RelativeSource={RelativeSource TemplatedParent}}'/>
              <Setter TargetName='Bd' Property='BorderBrush' Value='{Binding Tag.VividBrush, RelativeSource={RelativeSource TemplatedParent}}'/>
              <Setter Property='Foreground' Value='#22252f'/>
            </Trigger>
            <Trigger Property='IsChecked' Value='True'>
              <Setter TargetName='Bd' Property='Background' Value='{Binding Tag.VividBrush, RelativeSource={RelativeSource TemplatedParent}}'/>
              <Setter TargetName='Bd' Property='BorderBrush' Value='{Binding Tag.VividBrush, RelativeSource={RelativeSource TemplatedParent}}'/>
              <Setter Property='Foreground' Value='#22252f'/>
            </Trigger>
            <Trigger Property='IsKeyboardFocused' Value='True'>
              <Setter TargetName='Ring' Property='Visibility' Value='Visible'/>
            </Trigger>
            <Trigger Property='IsEnabled' Value='False'>
              <Setter Property='Opacity' Value='0.45'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='Tile' TargetType='RadioButton'>
    <Setter Property='FontFamily' Value='{StaticResource UiFont}'/>
    <Setter Property='FontSize' Value='14'/>
    <Setter Property='FontWeight' Value='Bold'/>
    <Setter Property='Foreground' Value='#4b5160'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='RadioButton'>
          <Grid>
            <Border x:Name='Ring' BorderBrush='#3d7bff' BorderThickness='2' CornerRadius='21' Margin='-4' Visibility='Collapsed'/>
            <Border x:Name='Bd' Background='#ffffff' BorderBrush='#e3ddd3' BorderThickness='2' CornerRadius='17' Padding='6,6,6,8'>
              <ContentPresenter HorizontalAlignment='Stretch' VerticalAlignment='Center' RecognizesAccessKey='False'/>
            </Border>
            <Grid x:Name='Check' Width='22' Height='22' HorizontalAlignment='Right' VerticalAlignment='Top' Margin='0,-6,-6,0' Visibility='Collapsed'>
              <Ellipse Fill='{Binding Tag.VividBrush, RelativeSource={RelativeSource TemplatedParent}}' Stroke='#ffffff' StrokeThickness='2'/>
              <Path Data='M6.5,11.2 L9.6,14.2 L15.5,8' Stroke='#22252f' StrokeThickness='2' StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round'/>
            </Grid>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='Bd' Property='BorderBrush' Value='{Binding Tag.VividBrush, RelativeSource={RelativeSource TemplatedParent}}'/>
              <Setter Property='Foreground' Value='#22252f'/>
            </Trigger>
            <Trigger Property='IsChecked' Value='True'>
              <Setter TargetName='Bd' Property='Background' Value='{Binding Tag.TintBrush, RelativeSource={RelativeSource TemplatedParent}}'/>
              <Setter TargetName='Bd' Property='BorderBrush' Value='{Binding Tag.VividBrush, RelativeSource={RelativeSource TemplatedParent}}'/>
              <Setter TargetName='Check' Property='Visibility' Value='Visible'/>
              <Setter Property='Foreground' Value='#22252f'/>
            </Trigger>
            <Trigger Property='IsKeyboardFocused' Value='True'>
              <Setter TargetName='Ring' Property='Visibility' Value='Visible'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='Choice' TargetType='RadioButton'>
    <Setter Property='FontFamily' Value='{StaticResource UiFont}'/>
    <Setter Property='FontSize' Value='14.5'/>
    <Setter Property='Foreground' Value='#22252f'/>
    <Setter Property='Background' Value='#ffffff'/>
    <Setter Property='BorderBrush' Value='#e3ddd3'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='HorizontalContentAlignment' Value='Stretch'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='RadioButton'>
          <Grid>
            <Border x:Name='Ring' BorderBrush='#3d7bff' BorderThickness='2' CornerRadius='19' Margin='-4' Visibility='Collapsed'/>
            <Border x:Name='Bd' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}'
                    BorderThickness='1.5' CornerRadius='15' Padding='14,11'>
              <DockPanel>
                <Grid DockPanel.Dock='Left' Width='22' Height='22' Margin='0,0,13,0' VerticalAlignment='Center'>
                  <Ellipse x:Name='Dot' Stroke='#cfc8bc' StrokeThickness='2' Fill='#ffffff'/>
                  <Ellipse x:Name='Pip' Width='9' Height='9' Fill='#22252f' Visibility='Collapsed'/>
                </Grid>
                <ContentPresenter VerticalAlignment='Center' RecognizesAccessKey='False'/>
              </DockPanel>
            </Border>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='Bd' Property='BorderBrush' Value='{Binding Tag.VividBrush, RelativeSource={RelativeSource TemplatedParent}}'/>
              <Setter TargetName='Dot' Property='Stroke' Value='{Binding Tag.VividBrush, RelativeSource={RelativeSource TemplatedParent}}'/>
            </Trigger>
            <Trigger Property='IsChecked' Value='True'>
              <Setter TargetName='Bd' Property='Background' Value='{Binding Tag.TintBrush, RelativeSource={RelativeSource TemplatedParent}}'/>
              <Setter TargetName='Bd' Property='BorderBrush' Value='{Binding Tag.VividBrush, RelativeSource={RelativeSource TemplatedParent}}'/>
              <Setter TargetName='Dot' Property='Stroke' Value='{Binding Tag.VividBrush, RelativeSource={RelativeSource TemplatedParent}}'/>
              <Setter TargetName='Dot' Property='Fill' Value='{Binding Tag.VividBrush, RelativeSource={RelativeSource TemplatedParent}}'/>
              <Setter TargetName='Pip' Property='Visibility' Value='Visible'/>
            </Trigger>
            <Trigger Property='IsKeyboardFocused' Value='True'>
              <Setter TargetName='Ring' Property='Visibility' Value='Visible'/>
            </Trigger>
            <Trigger Property='IsEnabled' Value='False'>
              <Setter Property='Opacity' Value='0.45'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='Bare' TargetType='RadioButton'>
    <Setter Property='FontFamily' Value='{StaticResource UiFont}'/>
    <Setter Property='FontSize' Value='14.5'/>
    <Setter Property='Foreground' Value='#22252f'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='HorizontalContentAlignment' Value='Stretch'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='RadioButton'>
          <Grid Background='Transparent'>
            <Border x:Name='Ring' BorderBrush='#3d7bff' BorderThickness='2' CornerRadius='17' Margin='-2' Visibility='Collapsed'/>
            <DockPanel Margin='16,14,16,14'>
              <Grid DockPanel.Dock='Left' Width='22' Height='22' Margin='0,0,14,0' VerticalAlignment='Center'>
                <Ellipse x:Name='Dot' Stroke='#cfc8bc' StrokeThickness='2' Fill='#ffffff'/>
                <Ellipse x:Name='Pip' Width='9' Height='9' Fill='#22252f' Visibility='Collapsed'/>
              </Grid>
              <ContentPresenter VerticalAlignment='Center' RecognizesAccessKey='False'/>
            </DockPanel>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='Dot' Property='Stroke' Value='{Binding Tag.VividBrush, RelativeSource={RelativeSource TemplatedParent}}'/>
            </Trigger>
            <Trigger Property='IsChecked' Value='True'>
              <Setter TargetName='Dot' Property='Stroke' Value='{Binding Tag.VividBrush, RelativeSource={RelativeSource TemplatedParent}}'/>
              <Setter TargetName='Dot' Property='Fill' Value='{Binding Tag.VividBrush, RelativeSource={RelativeSource TemplatedParent}}'/>
              <Setter TargetName='Pip' Property='Visibility' Value='Visible'/>
            </Trigger>
            <Trigger Property='IsKeyboardFocused' Value='True'>
              <Setter TargetName='Ring' Property='Visibility' Value='Visible'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- 설정 창 왼쪽 메뉴 한 줄: 쉴 때는 책상 위 글자, 마우스는 옅은 면, 고르면 흰 면 + 그 색 테두리 + 옅은 그림자 -->
  <Style x:Key='Nav' TargetType='RadioButton'>
    <Setter Property='FontFamily' Value='{StaticResource UiFont}'/>
    <Setter Property='FontSize' Value='14.5'/>
    <Setter Property='FontWeight' Value='Bold'/>
    <Setter Property='Foreground' Value='#4b5160'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='HorizontalContentAlignment' Value='Stretch'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='RadioButton'>
          <Grid>
            <Border x:Name='Ring' BorderBrush='#3d7bff' BorderThickness='2' CornerRadius='17' Margin='-3' Visibility='Collapsed'/>
            <Border x:Name='Shade' Background='#ffffff' CornerRadius='14' Visibility='Hidden'>
              <Border.Effect>
                <DropShadowEffect Color='#22252f' BlurRadius='16' ShadowDepth='3' Direction='270' Opacity='0.10'/>
              </Border.Effect>
            </Border>
            <Border x:Name='Bd' Background='Transparent' BorderBrush='Transparent' BorderThickness='1.5' CornerRadius='14' Padding='9,8,12,8'>
              <ContentPresenter VerticalAlignment='Center' RecognizesAccessKey='False'/>
            </Border>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='Bd' Property='Background' Value='#efeae2'/>
              <Setter Property='Foreground' Value='#22252f'/>
            </Trigger>
            <Trigger Property='IsChecked' Value='True'>
              <Setter TargetName='Shade' Property='Visibility' Value='Visible'/>
              <Setter TargetName='Bd' Property='Background' Value='#ffffff'/>
              <Setter TargetName='Bd' Property='BorderBrush' Value='{Binding Tag.VividBrush, RelativeSource={RelativeSource TemplatedParent}}'/>
              <Setter Property='Foreground' Value='#22252f'/>
            </Trigger>
            <Trigger Property='IsKeyboardFocused' Value='True'>
              <Setter TargetName='Ring' Property='Visibility' Value='Visible'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='CheckBox'>
    <Setter Property='FontFamily' Value='{StaticResource UiFont}'/>
    <Setter Property='FontSize' Value='14.5'/>
    <Setter Property='Foreground' Value='#22252f'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='CheckBox'>
          <Grid Background='Transparent'>
            <Grid.ColumnDefinitions>
              <ColumnDefinition Width='Auto'/>
              <ColumnDefinition Width='*'/>
            </Grid.ColumnDefinitions>
            <Grid Width='42' Height='24' VerticalAlignment='Top' Margin='0,0,12,0'>
              <Border x:Name='Ring' BorderBrush='#3d7bff' BorderThickness='2' CornerRadius='15' Margin='-3' Visibility='Collapsed'/>
              <Border x:Name='Track' CornerRadius='12' Background='#d9d3c8'/>
              <Ellipse x:Name='Knob' Width='18' Height='18' Fill='#ffffff' HorizontalAlignment='Left' Margin='3,0,0,0'>
                <Ellipse.RenderTransform><TranslateTransform X='0'/></Ellipse.RenderTransform>
                <Ellipse.Effect><DropShadowEffect Color='#22252f' BlurRadius='4' ShadowDepth='1' Direction='270' Opacity='0.28'/></Ellipse.Effect>
              </Ellipse>
            </Grid>
            <ContentPresenter Grid.Column='1' VerticalAlignment='Center' Margin='0,1,0,0' RecognizesAccessKey='False'/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='IsChecked' Value='True'>
              <Setter TargetName='Track' Property='Background' Value='#52d3a0'/>
              <Trigger.EnterActions>
                <BeginStoryboard>
                  <Storyboard>
                    <DoubleAnimation Storyboard.TargetName='Knob' Storyboard.TargetProperty='(UIElement.RenderTransform).(TranslateTransform.X)' To='18' Duration='0:0:0.2'>
                      <DoubleAnimation.EasingFunction><BackEase Amplitude='0.45' EasingMode='EaseOut'/></DoubleAnimation.EasingFunction>
                    </DoubleAnimation>
                  </Storyboard>
                </BeginStoryboard>
              </Trigger.EnterActions>
              <Trigger.ExitActions>
                <BeginStoryboard>
                  <Storyboard>
                    <DoubleAnimation Storyboard.TargetName='Knob' Storyboard.TargetProperty='(UIElement.RenderTransform).(TranslateTransform.X)' To='0' Duration='0:0:0.2'>
                      <DoubleAnimation.EasingFunction><BackEase Amplitude='0.45' EasingMode='EaseOut'/></DoubleAnimation.EasingFunction>
                    </DoubleAnimation>
                  </Storyboard>
                </BeginStoryboard>
              </Trigger.ExitActions>
            </Trigger>
            <MultiTrigger>
              <MultiTrigger.Conditions>
                <Condition Property='IsMouseOver' Value='True'/>
                <Condition Property='IsChecked' Value='False'/>
              </MultiTrigger.Conditions>
              <Setter TargetName='Track' Property='Background' Value='#c9c2b6'/>
            </MultiTrigger>
            <Trigger Property='IsKeyboardFocused' Value='True'>
              <Setter TargetName='Ring' Property='Visibility' Value='Visible'/>
            </Trigger>
            <Trigger Property='IsEnabled' Value='False'>
              <Setter Property='Opacity' Value='0.45'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='Field' TargetType='PasswordBox'>
    <Setter Property='FontFamily' Value='Consolas, Malgun Gothic'/>
    <Setter Property='FontSize' Value='14.5'/>
    <Setter Property='Height' Value='46'/>
    <Setter Property='Foreground' Value='#22252f'/>
    <Setter Property='CaretBrush' Value='#3d7bff'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='PasswordBox'>
          <Border x:Name='Bd' Background='#ffffff' BorderBrush='#ddd7cd' BorderThickness='1.5' CornerRadius='12'>
            <ScrollViewer x:Name='PART_ContentHost' Margin='14,0' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='Bd' Property='BorderBrush' Value='#bfb8ac'/>
            </Trigger>
            <Trigger Property='IsKeyboardFocused' Value='True'>
              <Setter TargetName='Bd' Property='BorderBrush' Value='#3d7bff'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='TextField' TargetType='TextBox'>
    <Setter Property='FontFamily' Value='{StaticResource UiFont}'/>
    <Setter Property='FontSize' Value='14.5'/>
    <Setter Property='Height' Value='46'/>
    <Setter Property='Foreground' Value='#22252f'/>
    <Setter Property='CaretBrush' Value='#3d7bff'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='TextBox'>
          <Border x:Name='Bd' Background='#ffffff' BorderBrush='#ddd7cd' BorderThickness='1.5' CornerRadius='12'>
            <Grid>
              <TextBlock x:Name='Hint' Text='{Binding Tag, RelativeSource={RelativeSource TemplatedParent}}' Foreground='#9a9fab'
                         Margin='16,0' VerticalAlignment='Center' IsHitTestVisible='False' Visibility='Collapsed' TextTrimming='CharacterEllipsis'/>
              <ScrollViewer x:Name='PART_ContentHost' Margin='14,0' VerticalAlignment='Center'/>
            </Grid>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='Text' Value=''>
              <Setter TargetName='Hint' Property='Visibility' Value='Visible'/>
            </Trigger>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='Bd' Property='BorderBrush' Value='#bfb8ac'/>
            </Trigger>
            <Trigger Property='IsKeyboardFocused' Value='True'>
              <Setter TargetName='Bd' Property='BorderBrush' Value='#3d7bff'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='ScrollThumb' TargetType='Thumb'>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Thumb'>
          <Border x:Name='T' Background='#d9d3c8' CornerRadius='4' Margin='3,2'/>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='T' Property='Background' Value='#bdb6aa'/>
            </Trigger>
            <Trigger Property='IsDragging' Value='True'>
              <Setter TargetName='T' Property='Background' Value='#a39c90'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style x:Key='ScrollPage' TargetType='RepeatButton'>
    <Setter Property='Focusable' Value='False'/>
    <Setter Property='IsTabStop' Value='False'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='RepeatButton'>
          <Border Background='Transparent'/>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='SliderThumb' TargetType='Thumb'>
    <Setter Property='Focusable' Value='False'/>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Thumb'>
          <Grid Width='20' Height='20' Background='Transparent'>
            <Ellipse x:Name='K' Fill='#ffffff' Stroke='#22252f' StrokeThickness='2'/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='K' Property='Fill' Value='#faf7f2'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style x:Key='SliderDone' TargetType='RepeatButton'>
    <Setter Property='Focusable' Value='False'/>
    <Setter Property='IsTabStop' Value='False'/>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='RepeatButton'>
          <Grid Background='Transparent'>
            <Border Height='6' CornerRadius='3' VerticalAlignment='Center' Margin='0,0,-6,0'
                    Background='{Binding Tag.VividBrush, RelativeSource={RelativeSource AncestorType=Slider}}'/>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType='Slider'>
    <Setter Property='Height' Value='28'/>
    <Setter Property='IsMoveToPointEnabled' Value='True'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Slider'>
          <Grid Background='Transparent'>
            <Border x:Name='Ring' BorderBrush='#3d7bff' BorderThickness='2' CornerRadius='8' Margin='-3,0' Visibility='Collapsed'/>
            <Border Height='6' CornerRadius='3' Background='#e6e0d6' VerticalAlignment='Center' Margin='2,0'/>
            <Track x:Name='PART_Track' Orientation='Horizontal' Margin='2,0'>
              <Track.DecreaseRepeatButton>
                <RepeatButton Command='Slider.DecreaseLarge' Style='{StaticResource SliderDone}'/>
              </Track.DecreaseRepeatButton>
              <Track.Thumb><Thumb Style='{StaticResource SliderThumb}'/></Track.Thumb>
              <Track.IncreaseRepeatButton>
                <RepeatButton Command='Slider.IncreaseLarge' Style='{StaticResource ScrollPage}'/>
              </Track.IncreaseRepeatButton>
            </Track>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='IsKeyboardFocused' Value='True'>
              <Setter TargetName='Ring' Property='Visibility' Value='Visible'/>
            </Trigger>
            <Trigger Property='IsEnabled' Value='False'>
              <Setter Property='Opacity' Value='0.45'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='ToolTab' TargetType='RadioButton'>
    <Setter Property='FontFamily' Value='{StaticResource UiFont}'/>
    <Setter Property='FontSize' Value='12.5'/>
    <Setter Property='FontWeight' Value='Bold'/>
    <Setter Property='Foreground' Value='#4b5160'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='RadioButton'>
          <Grid>
            <Border x:Name='Ring' BorderBrush='#3d7bff' BorderThickness='2' CornerRadius='16' Margin='-2' Visibility='Collapsed'/>
            <Border x:Name='Bd' Background='Transparent' CornerRadius='14' Padding='4,8,4,7'>
              <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' RecognizesAccessKey='False'/>
            </Border>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='Bd' Property='Background' Value='#efeae2'/>
              <Setter Property='Foreground' Value='#22252f'/>
            </Trigger>
            <Trigger Property='IsChecked' Value='True'>
              <Setter TargetName='Bd' Property='Background' Value='{Binding Tag.VividBrush, RelativeSource={RelativeSource TemplatedParent}}'/>
              <Setter Property='Foreground' Value='#22252f'/>
            </Trigger>
            <Trigger Property='IsKeyboardFocused' Value='True'>
              <Setter TargetName='Ring' Property='Visibility' Value='Visible'/>
            </Trigger>
            <Trigger Property='IsEnabled' Value='False'>
              <Setter Property='Opacity' Value='0.45'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='Swatch' TargetType='RadioButton'>
    <Setter Property='Width' Value='32'/>
    <Setter Property='Height' Value='32'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='RadioButton'>
          <Grid Background='Transparent'>
            <Ellipse x:Name='Outer' Stroke='Transparent' StrokeThickness='2.5'/>
            <Ellipse x:Name='Dot' Margin='5' Fill='{TemplateBinding Background}' Stroke='#d4cdc1' StrokeThickness='1'/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='Outer' Property='Stroke' Value='#bfb8ac'/>
            </Trigger>
            <Trigger Property='IsChecked' Value='True'>
              <Setter TargetName='Outer' Property='Stroke' Value='#22252f'/>
            </Trigger>
            <Trigger Property='IsKeyboardFocused' Value='True'>
              <Setter TargetName='Outer' Property='Stroke' Value='#3d7bff'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='ScrollBar'>
    <Setter Property='Width' Value='14'/>
    <Setter Property='MinWidth' Value='14'/>
    <Setter Property='Background' Value='Transparent'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ScrollBar'>
          <Track x:Name='PART_Track' IsDirectionReversed='True'>
            <Track.DecreaseRepeatButton><RepeatButton Style='{StaticResource ScrollPage}' Command='ScrollBar.PageUpCommand'/></Track.DecreaseRepeatButton>
            <Track.Thumb><Thumb Style='{StaticResource ScrollThumb}'/></Track.Thumb>
            <Track.IncreaseRepeatButton><RepeatButton Style='{StaticResource ScrollPage}' Command='ScrollBar.PageDownCommand'/></Track.IncreaseRepeatButton>
          </Track>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
    <Style.Triggers>
      <Trigger Property='Orientation' Value='Horizontal'>
        <Setter Property='Width' Value='Auto'/>
        <Setter Property='MinWidth' Value='0'/>
        <Setter Property='Height' Value='14'/>
        <Setter Property='MinHeight' Value='14'/>
        <Setter Property='Template'>
          <Setter.Value>
            <ControlTemplate TargetType='ScrollBar'>
              <Track x:Name='PART_Track' IsDirectionReversed='False'>
                <Track.DecreaseRepeatButton><RepeatButton Style='{StaticResource ScrollPage}' Command='ScrollBar.PageLeftCommand'/></Track.DecreaseRepeatButton>
                <Track.Thumb><Thumb Style='{StaticResource ScrollThumb}'/></Track.Thumb>
                <Track.IncreaseRepeatButton><RepeatButton Style='{StaticResource ScrollPage}' Command='ScrollBar.PageRightCommand'/></Track.IncreaseRepeatButton>
              </Track>
            </ControlTemplate>
          </Setter.Value>
        </Setter>
      </Trigger>
    </Style.Triggers>
  </Style>

  <Style TargetType='ToolTip'>
    <Setter Property='FontFamily' Value='{StaticResource UiFont}'/>
    <Setter Property='FontSize' Value='13'/>
    <Setter Property='Foreground' Value='#ffffff'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ToolTip'>
          <Border Background='#22252f' CornerRadius='9' Padding='10,6' Margin='0,0,0,0'>
            <ContentPresenter/>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='Hyperlink'>
    <Setter Property='Foreground' Value='#2259b8'/>
    <Setter Property='TextDecorations' Value='{x:Null}'/>
    <Style.Triggers>
      <Trigger Property='IsMouseOver' Value='True'>
        <Setter Property='TextDecorations' Value='Underline'/>
      </Trigger>
    </Style.Triggers>
  </Style>
</ResourceDictionary>";

        public static void Install(Application app)
        {
            var dict = (ResourceDictionary)XamlReader.Parse(Look.Restyle(Xaml, Look.Current));
            app.Resources.MergedDictionaries.Add(dict);
        }

        public static Style GetStyle(string key)
        {
            return (Style)Application.Current.Resources[key];
        }

        // ── 창 다듬기 ──

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// 일반 창: 책상색 바탕, 또렷한 글자, 윈도우 11에서는 제목 줄도 책상색으로.
        public static void Polish(Window w)
        {
            w.FontFamily = Font;
            w.Background = Desk;
            w.Foreground = Ink;
            TextOptions.SetTextFormattingMode(w, TextFormattingMode.Display);
            w.SourceInitialized += delegate
            {
                try
                {
                    IntPtr h = new WindowInteropHelper(w).Handle;
                    int caption = ColorRef(DeskColor);
                    int text = ColorRef(InkColor);
                    DwmSetWindowAttribute(h, 35, ref caption, 4);
                    DwmSetWindowAttribute(h, 36, ref text, 4);
                }
                catch { }
            };
        }

        static int ColorRef(Color c)
        {
            return c.R | (c.G << 8) | (c.B << 16);
        }

        // ── 화면 조각을 코드로 만드는 도우미 ──

        /// 한글 낱말이 줄 끝에서 반으로 쪼개지지 않게(웹의 keep-all처럼), 띄어쓰기가 아닌 글자 사이마다
        /// 보이지 않는 '붙임 글자'(U+2060)를 넣는다. 줄은 띄어쓰기에서만 바뀌고, 칸보다 긴 낱말은 WPF가 알아서 자른다.
        /// 화면에 보이기만 하는 글(TextBlock)에만 쓴다. 복사되는 글 상자에는 쓰지 않는다.
        public static string Keep(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('⁠') >= 0) return s;
            // 화살표·가운뎃점은 앞 낱말에 붙여 줄 첫머리에 오지 않게(줄은 그 뒤에서 바뀐다)
            s = s.Replace(" →", " →").Replace(" ·", " ·");
            var sb = new System.Text.StringBuilder(s.Length * 2);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                sb.Append(c);
                if (i + 1 >= s.Length) break;
                char n = s[i + 1];
                if (char.IsWhiteSpace(c) || char.IsWhiteSpace(n) || char.IsHighSurrogate(c)) continue;
                sb.Append('⁠');
            }
            return sb.ToString();
        }

        public static TextBlock Text(string s, double size, FontWeight weight, Brush color)
        {
            return new TextBlock
            {
                Text = Keep(s),
                FontFamily = Font,
                FontSize = size,
                FontWeight = weight,
                Foreground = color,
                TextWrapping = TextWrapping.Wrap
            };
        }

        public static TextBlock Body(string s)
        {
            var t = Text(s, 14.5, FontWeights.Normal, Ink2);
            t.LineHeight = 22;
            return t;
        }

        public static TextBlock MetaLabel(string s)
        {
            var t = Text(s, 13, FontWeights.Normal, Ink3);
            t.LineHeight = 19.5;
            return t;
        }

        /// 창 머리: 로고 + 큰 제목 + 작은 설명
        public static FrameworkElement Head(string title, string sub)
        {
            var dock = new DockPanel();
            var logo = Logo(44);
            logo.VerticalAlignment = VerticalAlignment.Top;
            logo.Margin = new Thickness(0, 2, 14, 0);
            DockPanel.SetDock(logo, Dock.Left);
            dock.Children.Add(logo);
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(Text(title, 22, FontWeights.Bold, Ink));
            if (!string.IsNullOrEmpty(sub))
            {
                var s = MetaLabel(sub);
                s.FontSize = 13.5;
                s.Margin = new Thickness(0, 3, 0, 0);
                words.Children.Add(s);
            }
            dock.Children.Add(words);
            return dock;
        }

        /// 대화 창 머리: 색 칩 + 제목 + 설명
        public static FrameworkElement DialogHead(string title, string sub, Hue hue, string glyph)
        {
            var dock = new DockPanel();
            var chip = GlyphChip(glyph, hue, 44);
            chip.VerticalAlignment = VerticalAlignment.Top;
            chip.Margin = new Thickness(0, 1, 14, 0);
            DockPanel.SetDock(chip, Dock.Left);
            dock.Children.Add(chip);
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(Text(title, 20, FontWeights.Bold, Ink));
            if (!string.IsNullOrEmpty(sub))
            {
                var s = MetaLabel(sub);
                s.FontSize = 13.5;
                s.Margin = new Thickness(0, 3, 0, 0);
                words.Children.Add(s);
            }
            dock.Children.Add(words);
            return dock;
        }

        /// 칸 제목: 색 칩(그림) + 굵은 제목
        public static FrameworkElement SectionHead(string title, Hue hue, string glyph)
        {
            var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
            var chip = GlyphChip(glyph, hue, 32);
            chip.Margin = new Thickness(0, 0, 11, 0);
            DockPanel.SetDock(chip, Dock.Left);
            dock.Children.Add(chip);
            var t = Text(title, 16.5, FontWeights.Bold, Ink);
            t.VerticalAlignment = VerticalAlignment.Center;
            dock.Children.Add(t);
            return dock;
        }

        /// 흰 카드: 둥근 모서리 + 옅은 그림자. 그림자는 뒤판에만 걸어 글자가 흐려지지 않게 한다.
        public static FrameworkElement Card(UIElement child, double pad)
        {
            var g = new Grid();
            var shade = new Border { Background = Sheet, CornerRadius = new CornerRadius(Look.R(20)), Effect = Shadow(InkColor, 22, 3, 0.08) };
            var face = new Border
            {
                Background = Sheet,
                BorderBrush = Look.Outline > 0 ? Ink : Frozen(Look.Neutral(Hex(0xEFEAE2))),
                BorderThickness = new Thickness(Look.Outline > 0 ? Look.Outline : 1),
                CornerRadius = new CornerRadius(Look.R(20)),
                Padding = new Thickness(pad),
                Child = child
            };
            g.Children.Add(shade);
            g.Children.Add(face);
            return g;
        }

        public static Button MakeButton(string text, string style)
        {
            return new Button { Content = text, Style = GetStyle(style) };
        }

        /// 작은 꼬리표: 옅은 칠 + 깊은 색 굵은 글(예: 구독, 무료, 기본)
        public static Border Tag(string text, Hue hue)
        {
            var t = Text(text, 12, FontWeights.Bold, hue.DeepBrush);
            t.TextWrapping = TextWrapping.NoWrap;
            return new Border
            {
                Background = hue.TintBrush,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 2, 8, 3),
                VerticalAlignment = VerticalAlignment.Center,
                Child = t
            };
        }

        /// 켜고 끄는 스위치. 아래 줄에 작은 설명을 붙일 수 있다.
        public static CheckBox Switch(string title, string sub, bool value, Action<bool> changed)
        {
            var words = new StackPanel();
            var t = Text(title, 14.5, FontWeights.Normal, Ink);
            t.LineHeight = 21;
            words.Children.Add(t);
            if (!string.IsNullOrEmpty(sub))
            {
                var m = MetaLabel(sub);
                m.Margin = new Thickness(0, 1, 0, 0);
                words.Children.Add(m);
            }
            var box = new CheckBox { Content = words, IsChecked = value };
            box.Click += delegate { changed(box.IsChecked == true); };
            return box;
        }

        /// 명령·키처럼 그대로 옮겨 쓸 글: 옅은 면 위 고정폭 글자
        public static Border CodeBox(string text)
        {
            var t = new TextBlock
            {
                Text = text,
                FontFamily = MonoFont,
                FontSize = 14,
                Foreground = Ink,
                TextWrapping = TextWrapping.Wrap
            };
            return new Border
            {
                Background = Sheet2,
                BorderBrush = Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(14, 11, 14, 12),
                Child = t
            };
        }

        /// 단추 줄(왼쪽부터). 사이 간격 8.
        public static WrapPanel ButtonRow(params Button[] buttons)
        {
            var w = new WrapPanel();
            for (int i = 0; i < buttons.Length; i++)
            {
                if (buttons[i] == null) continue;
                buttons[i].Margin = new Thickness(0, 0, 8, 0);
                buttons[i].VerticalAlignment = VerticalAlignment.Center;
                w.Children.Add(buttons[i]);
            }
            return w;
        }

        /// 경고: 산호색 둥근 상자 + 느낌표
        public static Border Warning(string text)
        {
            var dock = new DockPanel();
            var mark = new Grid { Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 11, 0) };
            mark.Children.Add(new Ellipse { Fill = Coral.VividBrush });
            var bang = Text("!", 14, FontWeights.Bold, Ink);
            bang.HorizontalAlignment = HorizontalAlignment.Center;
            bang.VerticalAlignment = VerticalAlignment.Center;
            bang.Margin = new Thickness(0, -1, 0, 0);
            mark.Children.Add(bang);
            DockPanel.SetDock(mark, Dock.Left);
            dock.Children.Add(mark);
            var t = Text(text, 13.5, FontWeights.Normal, Ink);
            t.LineHeight = 21;
            t.Margin = new Thickness(0, 0, 0, 0);
            dock.Children.Add(t);
            return new Border
            {
                Background = Frozen(Hex(0xFFF1EE)),
                BorderBrush = Frozen(Hex(0xFFD2C9)),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(13, 11, 14, 11),
                Child = dock
            };
        }

        /// 알림 상자: 옅은 색 바탕
        public static Border Note(string text, Hue hue)
        {
            var t = Text(text, 13.5, FontWeights.Normal, Ink2);
            t.LineHeight = 21;
            return new Border
            {
                Background = hue.TintBrush,
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(14, 11, 14, 11),
                Child = t
            };
        }

        // ── 그림(아이콘): 24칸 격자에 그린 선 그림. 직접 그려 따로 허락받을 것이 없다. ──

        static readonly Dictionary<string, string> GlyphData = new Dictionary<string, string>
        {
            { "pdf", "M7,3 H14 L19,8 V20 A1,1 0 0 1 18,21 H7 A1,1 0 0 1 6,20 V4 A1,1 0 0 1 7,3 Z M14,3 V8 H19 M9,13 H16 M9,17 H14" },
            { "image", "M5,4 H19 A2,2 0 0 1 21,6 V18 A2,2 0 0 1 19,20 H5 A2,2 0 0 1 3,18 V6 A2,2 0 0 1 5,4 Z M3,16.5 L8,11.5 L13,16.5 M11.5,15 L14.5,12 L21,18.5 M14.5,8.5 A1.6,1.6 0 1 0 17.7,8.5 A1.6,1.6 0 1 0 14.5,8.5 Z" },
            { "format", "M4,8 H17 M14,5 L17,8 L14,11 M20,16 H7 M10,13 L7,16 L10,19" },
            { "shrink", "M4,4 L10,10 M10,5 V10 H5 M20,20 L14,14 M14,19 V14 H19" },
            { "text", "M4,8 V6 A2,2 0 0 1 6,4 H8 M16,4 H18 A2,2 0 0 1 20,6 V8 M20,16 V18 A2,2 0 0 1 18,20 H16 M8,20 H6 A2,2 0 0 1 4,18 V16 M8.5,9 H15.5 M12,9 V15.5" },
            { "settings", "M4,7 H10 M14,7 H20 M10,7 A2,2 0 1 0 14,7 A2,2 0 1 0 10,7 Z M4,17 H6 M10,17 H20 M6,17 A2,2 0 1 0 10,17 A2,2 0 1 0 6,17 Z" },
            { "close", "M7,7 L17,17 M17,7 L7,17" },
            { "shape", "M4.5,4 H9.5 A0.5,0.5 0 0 1 10,4.5 V9.5 A0.5,0.5 0 0 1 9.5,10 H4.5 A0.5,0.5 0 0 1 4,9.5 V4.5 A0.5,0.5 0 0 1 4.5,4 Z M14,7 A3,3 0 1 0 20,7 A3,3 0 1 0 14,7 Z M7,14 L10,19.5 H4 Z M14,14 H20 V20 H14 Z" },
            { "key", "M5,6 H19 A2,2 0 0 1 21,8 V16 A2,2 0 0 1 19,18 H5 A2,2 0 0 1 3,16 V8 A2,2 0 0 1 5,6 Z M7,10 H7.2 M11,10 H11.2 M15,10 H15.2 M8,14 H16" },
            { "spark", "M11,4 L12.6,9.4 L18,11 L12.6,12.6 L11,18 L9.4,12.6 L4,11 L9.4,9.4 Z M18.5,3.5 V7.5 M16.5,5.5 H20.5" },
            { "check", "M5,12.5 L10,17.5 L19,7.5" },
            { "link", "M14,4 H20 V10 M20,4 L11,13 M18,14 V18 A2,2 0 0 1 16,20 H6 A2,2 0 0 1 4,18 V8 A2,2 0 0 1 6,6 H10" },
            { "box", "M4,8 L12,4 L20,8 V16 L12,20 L4,16 Z M4,8 L12,12 L20,8 M12,12 V20" },
            { "archive", "M3.5,4 H20.5 V8 H3.5 Z M5,8 V20 H19 V8 M10,12 H14" },
            { "media", "M5,5 H19 A2,2 0 0 1 21,7 V17 A2,2 0 0 1 19,19 H5 A2,2 0 0 1 3,17 V7 A2,2 0 0 1 5,5 Z M10,9 L15.5,12 L10,15 Z" },
            { "record", "M5,4 H19 A2,2 0 0 1 21,6 V15 A2,2 0 0 1 19,17 H5 A2,2 0 0 1 3,15 V6 A2,2 0 0 1 5,4 Z M9,21 H15 M12,17 V21 M12,7.7 A2.8,2.8 0 1 0 12,13.3 A2.8,2.8 0 1 0 12,7.7 Z" },
            { "shield", "M12,3 L19,6 V11 C19,15.5 16,18.8 12,21 C8,18.8 5,15.5 5,11 V6 Z M9,12 L11,14 L15,10" },
            { "photo", "M5,19.5 L14.5,10 M13,8.5 L16,11.5 M18,3.5 L18.9,6.1 L21.5,7 L18.9,7.9 L18,10.5 L17.1,7.9 L14.5,7 L17.1,6.1 Z M8,3.5 V7 M6.2,5.2 H9.8" },
            { "pdftool", "M9,3 H15.5 L20,7.5 V17 A1,1 0 0 1 19,18 H9 A1,1 0 0 1 8,17 V4 A1,1 0 0 1 9,3 Z M15.5,3 V7.5 H20 M4,7 V20 A1,1 0 0 0 5,21 H15" },
            { "ai", "M11,4 L12.6,9.4 L18,11 L12.6,12.6 L11,18 L9.4,12.6 L4,11 L9.4,9.4 Z M18.5,3.5 V7.5 M16.5,5.5 H20.5 M18.5,17 V20 M17,18.5 H20" },
            { "blur", "M12,3.5 C12,3.5 5.5,10.5 5.5,14.8 A6.5,6.5 0 0 0 18.5,14.8 C18.5,10.5 12,3.5 12,3.5 Z M9,15 A3,3 0 0 0 11.5,17.8" },
            { "crop", "M7,3 V17 H21 M3,7 H17 V21" },
            { "rotate", "M20,11 A8,8 0 1 0 17.7,17.7 M20,5 V11 H14" },
            { "flip", "M12,3 V21 M8.5,7.5 L3.5,17 H8.5 Z M15.5,7.5 L20.5,17 H15.5 Z" },
            { "resize", "M4,10 V4 H10 M4,4 L10,10 M20,14 V20 H14 M20,20 L14,14" },
            { "adjust", "M12,8 A4,4 0 1 0 12,16 A4,4 0 1 0 12,8 Z M12,3 V5 M12,19 V21 M3,12 H5 M19,12 H21 M5.6,5.6 L7,7 M17,17 L18.4,18.4 M5.6,18.4 L7,17 M17,7 L18.4,5.6" },
            { "letter", "M5,7 V4.5 H19 V7 M12,4.5 V19.5 M9,19.5 H15" },
            { "arrow", "M5,19 L19,5 M10,5 H19 V14" },
            { "marker", "M4,20.5 H13 M14.5,4.5 L19.5,9.5 L11,18 H6 V13 Z M12.5,6.5 L17.5,11.5" },
            { "frame", "M6,3 H18 A3,3 0 0 1 21,6 V18 A3,3 0 0 1 18,21 H6 A3,3 0 0 1 3,18 V6 A3,3 0 0 1 6,3 Z M8.5,8 H15.5 A0.5,0.5 0 0 1 16,8.5 V15.5 A0.5,0.5 0 0 1 15.5,16 H8.5 A0.5,0.5 0 0 1 8,15.5 V8.5 A0.5,0.5 0 0 1 8.5,8 Z" },
            { "info", "M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 Z M12,11 V16.5 M12,7.6 H12.1" },
            { "undo", "M8,5 L3.5,9.5 L8,14 M4,9.5 H15 A5,5 0 0 1 15,19.5 H10" },
            { "redo", "M16,5 L20.5,9.5 L16,14 M20,9.5 H9 A5,5 0 0 0 9,19.5 H14" },
            { "compare", "M12,3 V21 M9,6 H6 A1,1 0 0 0 5,7 V17 A1,1 0 0 0 6,18 H9 M15,6 H18 A1,1 0 0 1 19,7 V17 A1,1 0 0 1 18,18 H15" },
            { "scan", "M4,8 V5 A1,1 0 0 1 5,4 H8 M16,4 H19 A1,1 0 0 1 20,5 V8 M20,16 V19 A1,1 0 0 1 19,20 H16 M8,20 H5 A1,1 0 0 1 4,19 V16 M4,12 H20" },
            { "face", "M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 Z M9,10 H9.1 M15,10 H15.1 M8.5,15 C9.5,16.6 14.5,16.6 15.5,15" },
            { "lock", "M6,11 H18 A1,1 0 0 1 19,12 V20 A1,1 0 0 1 18,21 H6 A1,1 0 0 1 5,20 V12 A1,1 0 0 1 6,11 Z M8,11 V7 A4,4 0 0 1 16,7 V11" },
            { "star", "M12,3.5 L14.6,9 L20.5,9.8 L16.2,14 L17.3,20 L12,17.1 L6.7,20 L7.8,14 L3.5,9.8 L9.4,9 Z" },
            { "trash", "M4,7 H20 M9,7 V4.5 H15 V7 M6.5,7 L7.5,20 H16.5 L17.5,7" },
            { "copy", "M9,9 H19 A1,1 0 0 1 20,10 V20 A1,1 0 0 1 19,21 H9 A1,1 0 0 1 8,20 V10 A1,1 0 0 1 9,9 Z M5,15 H4 V4 H15 V5" },
            { "collage", "M5,4 H10 A1,1 0 0 1 11,5 V10 A1,1 0 0 1 10,11 H5 A1,1 0 0 1 4,10 V5 A1,1 0 0 1 5,4 Z M14,4 H19 A1,1 0 0 1 20,5 V19 A1,1 0 0 1 19,20 H14 A1,1 0 0 1 13,19 V5 A1,1 0 0 1 14,4 Z M5,13 H10 A1,1 0 0 1 11,14 V19 A1,1 0 0 1 10,20 H5 A1,1 0 0 1 4,19 V14 A1,1 0 0 1 5,13 Z" },
            { "plus", "M12,5 V19 M5,12 H19" },
            { "down", "M12,4 V15 M7.5,10.5 L12,15 L16.5,10.5 M5,20 H19" },
            { "up", "M6,14 L12,8 L18,14" },
            { "chevdown", "M6,10 L12,16 L18,10" },
            { "left", "M15,5 L8,12 L15,19" },
            { "right", "M9,5 L16,12 L9,19" },
            { "book", "M12,6.5 C10,4.8 7,4.3 3.5,4.5 V18.5 C7,18.3 10,18.8 12,20.5 C14,18.8 17,18.3 20.5,18.5 V4.5 C17,4.3 14,4.8 12,6.5 Z M12,6.5 V20.5" },
            { "bulb", "M9,18 H15 M10,21 H14 M12,3 A6.5,6.5 0 0 0 8.4,14.9 C8.9,15.4 9,16.2 9,18 M15,18 C15,16.2 15.1,15.4 15.6,14.9 A6.5,6.5 0 0 0 12,3" },
            { "people", "M3.5,20 V18.5 A4,4 0 0 1 7.5,14.5 H11.5 A4,4 0 0 1 15.5,18.5 V20 M6,7.5 A3.5,3.5 0 1 0 13,7.5 A3.5,3.5 0 1 0 6,7.5 Z M16.5,4.4 A3.5,3.5 0 0 1 16.5,10.6 M18,14.7 A4,4 0 0 1 20.5,18.5 V20" },
            { "clock", "M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 Z M12,7 V12 L15.5,14" },
            { "flag", "M5.5,21 V4 M5.5,4.5 H18 L15.5,8.5 L18,12.5 H5.5" },
            { "target", "M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 Z M12,7.5 A4.5,4.5 0 1 0 12,16.5 A4.5,4.5 0 1 0 12,7.5 Z M12,12 H12.1" },
            { "pencil", "M4,20 L5,15.5 L16,4.5 L19.5,8 L8.5,19 Z M14,6.5 L17.5,10" },
            { "chat", "M5.5,4 H18.5 A1.5,1.5 0 0 1 20,5.5 V15.5 A1.5,1.5 0 0 1 18.5,17 H10 L5.5,20.5 V17 A1.5,1.5 0 0 1 4,15.5 V5.5 A1.5,1.5 0 0 1 5.5,4 Z" },
            { "question", "M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 Z M9.3,9.3 A2.8,2.8 0 1 1 13.6,11.7 C12.6,12.3 12,12.9 12,14 M12,17.2 H12.1" },
            // 영상 편집
            { "cut", "M6,4.5 A2.5,2.5 0 1 0 6,9.5 A2.5,2.5 0 1 0 6,4.5 Z M6,14.5 A2.5,2.5 0 1 0 6,19.5 A2.5,2.5 0 1 0 6,14.5 Z M8.1,8.4 L20,17.5 M8.1,15.6 L20,6.5" },
            { "music", "M9,17.5 V5.5 L19,3.5 V15.5 M9,17.5 A2.5,2.5 0 1 1 4,17.5 A2.5,2.5 0 1 1 9,17.5 Z M19,15.5 A2.5,2.5 0 1 1 14,15.5 A2.5,2.5 0 1 1 19,15.5 Z M9,9 L19,7" },
            { "play", "M8,5 L19,12 L8,19 Z" },
            { "pause", "M8,5 V19 M16,5 V19" },
            { "mic", "M12,3.5 A3,3 0 0 1 15,6.5 V11.5 A3,3 0 0 1 9,11.5 V6.5 A3,3 0 0 1 12,3.5 Z M5.5,11 A6.5,6.5 0 0 0 18.5,11 M12,17.5 V20.5 M9,20.5 H15" },
            { "speed", "M4.5,16.5 A8,8 0 1 1 19.5,16.5 M12,13.5 L16,8.5" },
            { "first", "M6,5 V19 M18,5 L9,12 L18,19 Z" },
            { "last", "M18,5 V19 M6,5 L15,12 L6,19 Z" },
            { "stepback", "M17,6 L10,12 L17,18 M7,6 V18" },
            { "stepfwd", "M7,6 L14,12 L7,18 M17,6 V18" },
            { "sticker", "M5,4 H19 A1,1 0 0 1 20,5 V13 L13,20 H5 A1,1 0 0 1 4,19 V5 A1,1 0 0 1 5,4 Z M13,20 V14 A1,1 0 0 1 14,13 H20" },
            { "magnet", "M6,4 V11 A6,6 0 0 0 18,11 V4 M6,8 H9.5 M14.5,8 H18 M9.5,4 V11 A2.5,2.5 0 0 0 14.5,11 V4" },
            { "keyframe", "M12,4 L20,12 L12,20 L4,12 Z" },
            { "zoomin", "M10.5,4 A6.5,6.5 0 1 0 10.5,17 A6.5,6.5 0 1 0 10.5,4 Z M15.2,15.2 L20,20 M10.5,8 V13 M8,10.5 H13" },
            { "zoomout", "M10.5,4 A6.5,6.5 0 1 0 10.5,17 A6.5,6.5 0 1 0 10.5,4 Z M15.2,15.2 L20,20 M8,10.5 H13" },
            { "volume", "M4,9.5 H7.5 L12,5.5 V18.5 L7.5,14.5 H4 Z M15.5,9 A4,4 0 0 1 15.5,15 M18,6.5 A7.5,7.5 0 0 1 18,17.5" },
            { "mute", "M4,9.5 H7.5 L12,5.5 V18.5 L7.5,14.5 H4 Z M15.5,9.5 L20.5,14.5 M20.5,9.5 L15.5,14.5" },
            { "subtitle", "M5,5 H19 A2,2 0 0 1 21,7 V17 A2,2 0 0 1 19,19 H5 A2,2 0 0 1 3,17 V7 A2,2 0 0 1 5,5 Z M7,12.5 H11 M13,12.5 H17 M7,15.5 H14" },
            { "ratio", "M8,3.5 H16 A1.5,1.5 0 0 1 17.5,5 V19 A1.5,1.5 0 0 1 16,20.5 H8 A1.5,1.5 0 0 1 6.5,19 V5 A1.5,1.5 0 0 1 8,3.5 Z M3,9 V15 M21,9 V15" },
            { "teacher", "M3,9 L12,4.5 L21,9 L12,13.5 Z M7,11.2 V15.5 C7,17 9.2,18.5 12,18.5 C14.8,18.5 17,17 17,15.5 V11.2 M21,9 V14" },
            { "export", "M12,15 V4 M7.5,8.5 L12,4 L16.5,8.5 M5,13 V19 A1,1 0 0 0 6,20 H18 A1,1 0 0 0 19,19 V13" },
            { "split", "M12,3 V21 M4,7 H9 V17 H4 M20,7 H15 V17 H20" },
            { "safe", "M4,4 H20 V20 H4 Z M7,7.5 H17 M7,16.5 H17" },
            { "wave", "M3,12 H5 M7,8 V16 M10,5 V19 M13,9 V15 M16,6.5 V17.5 M19,10 V14 M21,12 H21.1" },
            { "range", "M5,5 V19 M19,5 V19 M5,12 H19 M9,9 L5,12 L9,15 M15,9 L19,12 L15,15" }
        };

        static readonly Dictionary<string, Geometry> GlyphCache = new Dictionary<string, Geometry>();

        public static Geometry GlyphGeometry(string id)
        {
            // 슬라이드를 만드는 일꾼 스레드에서도 부른다.
            lock (GlyphCache)
            {
                Geometry g;
                if (GlyphCache.TryGetValue(id ?? "", out g)) return g;
                string data;
                if (id == null || !GlyphData.TryGetValue(id, out data)) data = GlyphData["box"];
                g = Geometry.Parse(data);
                g.Freeze();
                GlyphCache[id ?? ""] = g;
                return g;
            }
        }

        /// 그림 하나(size×size). 선 굵기는 화면 픽셀 기준.
        public static FrameworkElement Glyph(string id, double size, Brush stroke, double thickness)
        {
            var g = GlyphGeometry(id).Clone();
            g.Transform = new ScaleTransform(size / 24.0, size / 24.0);
            var path = new ShapePath
            {
                Data = g,
                Stroke = stroke,
                StrokeThickness = thickness,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = size,
                Height = size,
                IsHitTestVisible = false,
                SnapsToDevicePixels = false
            };
            return path;
        }

        /// 둥근 네모 칩 안의 그림: 옅은 칠 + 깊은 색 선
        public static FrameworkElement GlyphChip(string id, Hue hue, double size)
        {
            var g = new Grid { Width = size, Height = size };
            g.Children.Add(new Border { Background = hue.TintBrush, CornerRadius = new CornerRadius(size * 0.32) });
            var icon = Glyph(id, size * 0.56, hue.DeepBrush, size >= 40 ? 2.1 : 1.9);
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(icon);
            return g;
        }

        /// 바꿈 로고: 잉크색 둥근 판 위 색인 띠 세 장(가운데 띠가 쏙 빠져나옴)
        public static FrameworkElement Logo(double size)
        {
            var c = new Canvas { Width = size, Height = size, IsHitTestVisible = false };
            c.Children.Add(new Border { Width = size, Height = size, Background = Ink, CornerRadius = new CornerRadius(size * 0.26) });
            double h = size * 0.135;
            AddBar(c, size * 0.20, size * 0.255, size * 0.44, h, Coral.VividBrush);
            AddBar(c, size * 0.20, size * 0.4325, size * 0.60, h, Yellow.VividBrush);
            AddBar(c, size * 0.20, size * 0.61, size * 0.36, h, Blue.VividBrush);
            return c;
        }

        static void AddBar(Canvas c, double x, double y, double w, double h, Brush fill)
        {
            var b = new Border { Width = w, Height = h, Background = fill, CornerRadius = new CornerRadius(h / 2) };
            Canvas.SetLeft(b, x);
            Canvas.SetTop(b, y);
            c.Children.Add(b);
        }

    }

    /// 상태 알약: ● 연결됨 같은 짧은 상태 글. 색과 글을 바꿀 수 있다.
    public class StatusPill : Border
    {
        readonly Ellipse dot;
        readonly TextBlock text;
        string message = "";

        public StatusPill()
        {
            CornerRadius = new CornerRadius(11);
            Padding = new Thickness(10, 4, 12, 5);
            HorizontalAlignment = HorizontalAlignment.Left;
            var row = new DockPanel();
            dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 6, 7, 0), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(dot, Dock.Left);
            text = Theme.Text("", 13, FontWeights.Bold, Theme.Ink);
            text.LineHeight = 19;
            row.Children.Add(dot);
            row.Children.Add(text);
            Child = row;
            Set("", Theme.Slate);
        }

        public void Set(string message, Hue hue)
        {
            this.message = message ?? "";
            text.Text = Theme.Keep(this.message);
            text.Foreground = hue.DeepBrush;
            dot.Fill = hue.VividBrush;
            Background = hue.TintBrush;
            Visibility = this.message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        public string Message
        {
            get { return message; }
        }
    }
}
