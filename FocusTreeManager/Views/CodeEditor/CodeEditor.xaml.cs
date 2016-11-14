﻿//Modified version of http://syntaxhighlightbox.codeplex.com

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Windows.Input;
using System.Collections.Generic;
using System.Threading;
using FocusTreeManager.Helper;
using FocusTreeManager.CodeStructures.CodeEditor;
using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using FocusTreeManager.Containers;

namespace FocusTreeManager.Views.CodeEditor
{
	public partial class CodeEditor : TextBox
    {
        public static readonly DependencyProperty FoundBrushProperty =
        DependencyProperty.Register("FoundTextBrush", typeof(Brush), typeof(CodeEditor),
        new UIPropertyMetadata(Brushes.LightGoldenrodYellow));

        public Brush FoundTextBrush
        {
            get { return (Brush)GetValue(FoundBrushProperty); }
            set { SetValue(FoundBrushProperty, value); }
        }

        public static readonly DependencyProperty HighlightBrushProperty =
        DependencyProperty.Register("HighlightTextBrush", typeof(Brush), typeof(CodeEditor),
        new UIPropertyMetadata(Brushes.LightCyan));

        public Brush HighlightTextBrush
        {
            get { return (Brush)GetValue(HighlightBrushProperty); }
            set { SetValue(HighlightBrushProperty, value); }
        }

        public static readonly DependencyProperty BracketBrushProperty =
        DependencyProperty.Register("BracketBrush", typeof(Brush), typeof(CodeEditor),
        new UIPropertyMetadata(Brushes.Crimson));

        public Brush BracketBrush
        {
            get { return (Brush)GetValue(BracketBrushProperty); }
            set { SetValue(BracketBrushProperty, value); }
        }

        public static readonly DependencyProperty TabSizeProperty =
        DependencyProperty.Register("TabSize", typeof(int), typeof(CodeEditor),
        new UIPropertyMetadata(4));

        public int TabSize
        {
            get { return (int)GetValue(TabSizeProperty); }
            set { SetValue(TabSizeProperty, value); }
        }

        public double LineHeight
        {
			get
            {
                return lineHeight;
            }
			set
            {
				if (value != lineHeight)
                {
					lineHeight = value;
					blockHeight = MaxLineCountInBlock * value;
					TextBlock.SetLineStackingStrategy(this, LineStackingStrategy.BlockLineHeight);
					TextBlock.SetLineHeight(this, lineHeight);
				}
			}
		}

		public int MaxLineCountInBlock
        {
			get
            {
                return maxLineCountInBlock;
            }
			set
            {
				maxLineCountInBlock = value > 0 ? value : 0;
				blockHeight = value * LineHeight;
			}
        }

        public delegate void RenderedDelegate();

        public RenderedDelegate RenderMethod { get; set; }

        public delegate void TextUpdateDelegate(string NewText);

        public TextUpdateDelegate TextUpdated { get; set; }

        private CodeNavigator navigator;

        private DrawingControl renderCanvas;

		private DrawingControl lineNumbersCanvas;

		private ScrollViewer scrollViewer;

        private double lineHeight;

		private int totalLineCount;

		private List<InnerTextBlock> blocks;

		private double blockHeight;

		private int maxLineCountInBlock;

        private bool WaitingForClosingBracket = false;

        private Regex TextToHighlight;

        private Regex FoundText;

        public CodeEditor()
        {
			InitializeComponent();
			MaxLineCountInBlock = 100;
			LineHeight = FontSize * 1.3;
            TabSize = 4;
			totalLineCount = 1;
			blocks = new List<InnerTextBlock>();
			Loaded += (s, e) => {
                ApplyTemplate();
                Text = Text.Replace("\t", new string(' ', TabSize));
                renderCanvas = (DrawingControl)Template.FindName("PART_RenderCanvas", this);
                lineNumbersCanvas = (DrawingControl)Template.FindName("PART_LineNumbersCanvas", this);
				scrollViewer = (ScrollViewer)Template.FindName("PART_ContentHost", this);
                lineNumbersCanvas.Width = GetFormattedTextWidth(string.Format("{0:0000}", 
                    totalLineCount)) + 5;
				scrollViewer.ScrollChanged += OnScrollChanged;
				InvalidateBlocks(0);
				InvalidateVisual();
            };
			SizeChanged += (s, e) => {
                if (e.HeightChanged == false)
                {
                    return;
                }
				UpdateBlocks();
				InvalidateVisual();
			};
			TextChanged += (s, e) => {
				UpdateTotalLineCount();
				InvalidateBlocks(e.Changes.First().Offset);
				InvalidateVisual();
                //If navigator exists
                if (navigator != null)
                {
                    navigator.LinkedScrollViewerHeight = scrollViewer.ViewportHeight;
                    navigator.UpdateText(GetFormattedText(Text),
                        new Point(2 - HorizontalOffset, VerticalOffset),
                        scrollViewer.VerticalOffset);
                    TextUpdated(Text);
                }
            };
            PreviewTextInput += (s, e) => {
                if (e.Text.Contains("{"))
                {
                    WaitingForClosingBracket = true;
                }
                if (e.Text.Contains("}"))
                {
                    WaitingForClosingBracket = false;
                }
            };
            PreviewKeyDown += new KeyEventHandler(CodeEditor_OnPreviewKeyDown);
            SelectionChanged += new RoutedEventHandler(CodeEditor_SelectionChanged);
            PreviewMouseDoubleClick += new MouseButtonEventHandler(CodeEditor_MouseDoubleClick);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
			DrawBlocks();
			base.OnRender(drawingContext);
            //Render the navigator once the editor is ready
            if (navigator == null && scrollViewer != null && scrollViewer.ViewportHeight != 0)
            {
                navigator = new CodeNavigator(GetFormattedText(Text),
                    new Point(2 - HorizontalOffset, VerticalOffset));
                navigator.LinkedScrollViewerHeight = scrollViewer.ViewportHeight;
                navigator.ScrollMethod = new CodeNavigator.ScrollDelegate(Scroll);
                RenderMethod();
            }
        }

		private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.VerticalChange != 0)
            {
                UpdateBlocks();
            }
			InvalidateVisual();
            //If a navigator exists
            if (navigator != null)
            {
                navigator.setScrolling(scrollViewer.VerticalOffset);
            }
		}

        private void CodeEditor_OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            ManageTabs(e);
            ManageFormatting(e);
            if (e.Key == Key.Z && 
                (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 
                (ModifierKeys.Control | ModifierKeys.Alt))
            {
                this.Redo();
            }
        }

        private void CodeEditor_MouseDoubleClick(object sender,  MouseButtonEventArgs e)
        {
            int nextSpace = Text.IndexOf(' ', CaretIndex);
            int selectionStart = 0;
            string trimmedString = string.Empty;
            if (nextSpace != -1)
            {
                trimmedString = Text.Substring(0, nextSpace);
            }
            else
            {
                trimmedString = Text;
            }
            if (trimmedString.LastIndexOf(' ') != -1)
            {
                selectionStart = 1 + trimmedString.LastIndexOf(' ');
                trimmedString = trimmedString.Substring(1 + trimmedString.LastIndexOf(' '));
            }
            Select(selectionStart, trimmedString.Length);
        }

        #region BracketHiglightBlocks

        private int openBracketPos = -1;
        private int closeBracketPos = -1;

        #endregion

        private void CodeEditor_SelectionChanged(object sender, RoutedEventArgs e)
        {
            TextToHighlight = null;
            //Get caret position and highlight the blocks that are needed
            int caretPosition = CaretIndex;
            //If the caret is not valid, caret reset
            if (caretPosition < 1 || caretPosition >= Text.Length)
            {
                return;
            }
            //Check if the selected text is a word and fi we can highlight it
            if (SelectedText.IndexOf(' ') == -1)
            { 
                //If only accepted chars and is there more than once
                if (Regex.IsMatch(SelectedText, @"(?i)^[a-z_\-0-9]+")
                    && Regex.Matches(Text, SelectedText).Count > 1)
                {
                    TextToHighlight = new Regex(@"\b" + SelectedText + @"\b");
                }
            }
            //if the caret is near an opening bracket, the caret can be before or after the caret
            if (Text.Substring(caretPosition, 1).Contains("{"))
            {
                openBracketPos = caretPosition;
                closeBracketPos = CodeHelper.getAssociatedClosingBracket(Text, caretPosition);
            }
            else if (Text.Substring(caretPosition - 1, 1).Contains("{"))
            {
                caretPosition -= 1;
                openBracketPos = caretPosition;
                closeBracketPos = CodeHelper.getAssociatedClosingBracket(Text, caretPosition);
            }
            //else if the caret is near a closing bracket, the caret can be before or after the caret
            else if (Text.Substring(caretPosition, 1).Contains("}"))
            {
                openBracketPos = CodeHelper.getAssociatedOpeningBracket(Text, caretPosition);
                closeBracketPos = caretPosition;
            }
            else if (Text.Substring(caretPosition - 1, 1).Contains("}"))
            {
                caretPosition -= 1;
                openBracketPos = CodeHelper.getAssociatedOpeningBracket(Text, caretPosition);
                closeBracketPos = caretPosition;
            }
            else
            {
                openBracketPos = -1;
                closeBracketPos = -1;
            }
            InvalidateBlocks(caretPosition);
            InvalidateVisual();
        }

        private void ManageTabs(KeyEventArgs e)
        {
            if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None)
            {
                string tab = new string(' ', TabSize);
                //Check if text is selected
                if (!string.IsNullOrEmpty(SelectedText))
                {
                    StringBuilder builder = new StringBuilder(Text);
                    int caretStart = Text.IndexOf(SelectedText);
                    int Start = Text.Substring(0, Text.IndexOf(SelectedText)).LastIndexOf("\n");
                    //To calculate the real length from first \n to last \n with the selected text in the middle
                    //Extract the selected text from \n to end of selected text
                    string subtext = Text.Substring(Start, SelectedText.Length);
                    //Remove everything until the end of selected text
                    builder.Remove(0, Start + subtext.Length);
                    //Real length is equal to the subtext length plus distance to fist \n in cleaned builder
                    int RealLength = subtext.Length + builder.ToString().IndexOf("\n");
                    int pos = Start;
                    //if yes, add a tab at the beginning of the line;
                    foreach (string line in Text.Substring(Start, RealLength).Split('\n'))
                    {
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            continue;
                        }
                        builder = new StringBuilder(Text);
                        //For each line selected
                        builder.Remove(pos, line.Length + 1);
                        //Insert the tab at the start of the line plus repair the breakline
                        builder.Insert(pos, "\n" + tab + line);
                        Text = builder.ToString();
                        //Set the position to the start of text line + the size of what we added
                        pos += line.Length + TabSize + 1;
                    }
                    //Set the selected text and caret
                    CaretIndex = pos;
                    Select(caretStart + TabSize, pos - caretStart - TabSize);
                }
                else
                {
                    int caret = CaretIndex;
                    //Insert the tab
                    Text = Text.Insert(CaretIndex, tab);
                    //Place the caret where it was - the text removed
                    CaretIndex = caret + TabSize;
                }
                //Handle the event
                e.Handled = true;
            }
            else if (e.Key == Key.Tab && (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            {
                //Handle the event
                e.Handled = true;
                //Check if text is selected
                if (!string.IsNullOrEmpty(SelectedText))
                {
                    StringBuilder builder = new StringBuilder(Text);
                    int Start = Text.Substring(0, Text.IndexOf(SelectedText)).LastIndexOf("\n");
                    //Check if the first line has space to be moved without killing characters
                    if (!string.IsNullOrWhiteSpace(Text.Substring(Start, TabSize)))
                    {
                        //If there are chars, kill the event
                        return;
                    }
                    int caretStart = Text.IndexOf(SelectedText);
                    //To calculate the real length from first \n to last \n with the selected text in the middle
                    //Extract the selected text from \n to end of selected text
                    string subtext = Text.Substring(Start, SelectedText.Length);
                    //Remove everything until the end of selected text
                    builder.Remove(0, Start + subtext.Length);
                    //Real length is equal to the subtext length plus distance to fist \n in cleaned builder
                    int RealLength = subtext.Length + builder.ToString().IndexOf("\n");
                    int pos = Start;
                    //if yes, add a tab at the beginning of the line;
                    foreach (string line in Text.Substring(Start, RealLength).Split('\n'))
                    {
                        if (string.IsNullOrWhiteSpace(line) && line.Length <= TabSize)
                        {
                            continue;
                        }
                        builder = new StringBuilder(Text);
                        //For each line selected
                        builder.Remove(pos, line.Length + 1);
                        //Remove the tab at the start of the line plus repair the breakline
                        builder.Insert(pos, "\n" + line.Substring(TabSize));
                        Text = builder.ToString();
                        //Set the position to the start of text line - the size of what we removed
                        pos += line.Length - TabSize + 1;
                    }
                    //Set the selected text and caret
                    CaretIndex = pos;
                    Select(caretStart - TabSize, pos - caretStart + TabSize);
                }
                else
                {
                    int caret = CaretIndex;
                    //Remove a tab 
                    StringBuilder builder = new StringBuilder(Text);
                    //Get the position of line start with spaces
                    int Start = Text.Substring(0, CaretIndex).LastIndexOf("\n") + 1;
                    //Check if the first line has space to be moved without killing characters
                    if (!string.IsNullOrWhiteSpace(Text.Substring(Start, TabSize)))
                    {
                        //If there are chars, kill the event
                        return;
                    }
                    //Get the whole line
                    string subtext = Text.Substring(Start, Text.Substring(Start).IndexOf("\n"));
                    //Remove the line from the builder
                    builder.Remove(Start, subtext.Length);
                    //Add it again without the tab
                    builder.Insert(Start, subtext.Substring(TabSize));
                    Text = builder.ToString();
                    //Place the caret where it was - the text removed
                    CaretIndex = caret - TabSize;
                }
            }
        }

        private void ManageFormatting(KeyEventArgs e)
        {
            if (e.Key == Key.Return && Keyboard.Modifiers == ModifierKeys.None)
            {
                int caret = CaretIndex;
                //Handle the event
                e.Handled = true;
                //Get the indent level
                int indent = CodeHelper.getLevelOfIndent(Text.Substring(0, caret));
                //If needed, add a closing bracket
                if (WaitingForClosingBracket)
                {
                    Text = Text.Insert(caret, "\n" + new string(' ', TabSize * (indent - 1)) + "}");
                    WaitingForClosingBracket = false;
                }
                //Insert a number of tab equals to the indent level + 1 after the \n
                string tab = new string(' ', TabSize * (indent));
                Text = Text.Insert(caret, "\n" + tab);
                //Place the caret to the new position
                CaretIndex = caret + (TabSize * (indent)) + 1;
            }
        }

        #region PublicMethods

        public MatchCollection Find(Regex TextToFind, int index)
        {
            FoundText = TextToFind;
            foreach (InnerTextBlock block in blocks)
            {
                MatchCollection Selected = TextToFind.Matches(block.RawText);
                if (Selected.Count <= index)
                {
                    return Selected;
                }
                Match currentSelect = Selected[index];
                Select(block.CharStartIndex + currentSelect.Index, currentSelect.Length);
                ScrollToLine(Text.Substring(0, SelectionStart).Count(s => s == '\n'));
                return Selected;
            }
            return null;
        }

        public MatchCollection Replace(Regex TextToFind, string TextToReplace, int index)
        {
            FoundText = TextToFind;
            foreach (InnerTextBlock block in blocks)
            {
                MatchCollection Selected = TextToFind.Matches(block.RawText);
                if (Selected.Count <= index)
                {
                    return Selected;
                }
                Match currentSelect = Selected[index];
                Select(block.CharStartIndex + currentSelect.Index, currentSelect.Length);
                SelectedText = TextToReplace;
                ScrollToLine(Text.Substring(0, SelectionStart).Count(s => s == '\n'));
                return Selected;
            }
            return null;
        }

        public void EndFindAndReplace()
        {
            FoundText = null;
            InvalidateVisual();
        }

        public CodeNavigator GetNavigator()
        {
            return navigator;
        }

        public void Scroll(double verticalOffset)
        {
            scrollViewer.ScrollToVerticalOffset(verticalOffset);
        }

        public void Select(string ElementName, int Occurence)
        {
            int i = 0;
            foreach (Match word in Regex.Matches(Text, @"\b" + ElementName + @"\b"))
            {
                if (i == Occurence)
                {
                    Dispatcher.BeginInvoke(new ThreadStart(delegate ()
                    {
                        Select(word.Index, word.Length);
                        Focus();
                    }), null);
                    return;
                }
                i++;
            }
        }

        #endregion

        #region SyntaxHiglighting

        private void UpdateTotalLineCount()
        {
			totalLineCount = CodeHelper.GetLineCount(Text);
		}

		private void UpdateBlocks()
        {
			if (blocks.Count == 0)
            {
                return;
            }
			// While something is visible after last block
			while (!blocks.Last().IsLast && blocks.Last().Position.Y + blockHeight - VerticalOffset < ActualHeight)
            {
				int firstLineIndex = blocks.Last().LineEndIndex + 1;
				int lastLineIndex = firstLineIndex + maxLineCountInBlock - 1;
				lastLineIndex = lastLineIndex <= totalLineCount - 1 ? lastLineIndex : totalLineCount - 1;
				int fisrCharIndex = blocks.Last().CharEndIndex + 1;
				int lastCharIndex = CodeHelper.GetLastCharIndexFromLineIndex(Text, lastLineIndex);
				if (lastCharIndex <= fisrCharIndex)
                {
					blocks.Last().IsLast = true;
					return;
				}
				InnerTextBlock block = new InnerTextBlock(
					fisrCharIndex,
					lastCharIndex,
					blocks.Last().LineEndIndex + 1,
					lastLineIndex,
					LineHeight);
				block.RawText = block.GetSubString(Text);
				block.LineNumbers = GetFormattedLineNumbers(block.LineStartIndex, block.LineEndIndex);
				blocks.Add(block);
				FormatBlock(block, blocks.Count > 1 ? blocks[blocks.Count - 2] : null);
            }
        }

		private void InvalidateBlocks(int changeOffset)
        {
			InnerTextBlock blockChanged = null;
			for (int i = 0; i < blocks.Count; i++)
            {
				if (blocks[i].CharStartIndex <= changeOffset && changeOffset <= blocks[i].CharEndIndex + 1)
                {
					blockChanged = blocks[i];
					break;
				}
			}
			if (blockChanged == null && changeOffset > 0)
            {
                blockChanged = blocks.Last();
            }
			int fvline = blockChanged != null ? blockChanged.LineStartIndex : 0;
			int lvline = GetIndexOfLastVisibleLine();
			int fvchar = blockChanged != null ? blockChanged.CharStartIndex : 0;
			int lvchar = CodeHelper.GetLastCharIndexFromLineIndex(Text, lvline);
			if (blockChanged != null)
            {
                blocks.RemoveRange(blocks.IndexOf(blockChanged), blocks.Count - blocks.IndexOf(blockChanged));
            }
			int localLineCount = 1;
			int charStart = fvchar;
			int lineStart = fvline;
			for (int i = fvchar; i < Text.Length; i++)
            {
				if (Text[i] == '\n')
                {
					localLineCount += 1;
				}
				if (i == Text.Length - 1)
                {
					string blockText = Text.Substring(charStart);
					InnerTextBlock block = new InnerTextBlock(
						charStart,
						i, lineStart,
						lineStart + CodeHelper.GetLineCount(blockText) - 1,
						LineHeight);
					block.RawText = block.GetSubString(Text);
					block.LineNumbers = GetFormattedLineNumbers(block.LineStartIndex, block.LineEndIndex);
					block.IsLast = true;
					foreach (InnerTextBlock b in blocks)
                    {
                        if (b.LineStartIndex == block.LineStartIndex)
                        {
                            throw new Exception();
                        }
                    }
					blocks.Add(block);
					FormatBlock(block, blocks.Count > 1 ? blocks[blocks.Count - 2] : null);
                    break;
				}
				if (localLineCount > maxLineCountInBlock)
                {
					InnerTextBlock block = new InnerTextBlock(
						charStart,
						i,
						lineStart,
						lineStart + maxLineCountInBlock - 1,
						LineHeight);
					block.RawText = block.GetSubString(Text);
					block.LineNumbers = GetFormattedLineNumbers(block.LineStartIndex, block.LineEndIndex);
                    foreach (InnerTextBlock b in blocks)
                    {
                        if (b.LineStartIndex == block.LineStartIndex)
                        {
                            throw new Exception();
                        }
                    }
                    blocks.Add(block);
					FormatBlock(block, blocks.Count > 1 ? blocks[blocks.Count - 2] : null);
                    charStart = i + 1;
					lineStart += maxLineCountInBlock;
					localLineCount = 1;
					if (i > lvchar)
                    {
                        break;
                    }
				}
			}
        }

		private void DrawBlocks()
        {
			if (!IsLoaded || renderCanvas == null || lineNumbersCanvas == null)
            {
                return;
            }
			var dc = renderCanvas.GetContext();
			var dc2 = lineNumbersCanvas.GetContext();
			for (int i = 0; i < blocks.Count; i++)
            {
				InnerTextBlock block = blocks[i];
				Point blockPos = block.Position;
				double top = blockPos.Y - VerticalOffset;
				double bottom = top + blockHeight;
				if (top < ActualHeight && bottom > 0)
                {
                    try
                    {
                        if (TextToHighlight != null)
                        {
                            foreach (Match m in TextToHighlight.Matches(block.FormattedText.Text))
                            {
                                Geometry highlight = block.FormattedText.BuildHighlightGeometry(
                                        new Point(2 - HorizontalOffset, block.Position.Y - this.VerticalOffset),
                                        m.Index, m.Length);
                                if (highlight != null)
                                {
                                    Brush brush = HighlightTextBrush.Clone();
                                    brush.Opacity = 0.5;
                                    dc.DrawGeometry(brush, null, highlight);
                                }
                            }
                        }
                        if (FoundText != null)
                        {
                            foreach (Match m in FoundText.Matches(block.FormattedText.Text))
                            {
                                Geometry highlight = block.FormattedText.BuildHighlightGeometry(
                                        new Point(2 - HorizontalOffset, block.Position.Y - this.VerticalOffset),
                                        m.Index, m.Length);
                                if (highlight != null)
                                {
                                    Brush brush = FoundTextBrush.Clone();
                                    brush.Opacity = 0.5;
                                    dc.DrawGeometry(brush, null, highlight);
                                }
                            }
                        }
                        dc.DrawText(block.FormattedText, new Point(2 -
                            HorizontalOffset, block.Position.Y - VerticalOffset));
                        lineNumbersCanvas.Width = GetFormattedTextWidth(string.Format("{0:0000}", 
                            totalLineCount)) + 5;
						dc2.DrawText(block.LineNumbers, new Point(lineNumbersCanvas.ActualWidth, 1 + 
                            block.Position.Y - VerticalOffset));
					}
                    catch
                    {
                        //Strange exception with large Copy
					}
				}
			}
			dc.Close();
		    dc2.Close();
		}

		/// <summary>
		/// Returns the index of the first visible text line.
		/// </summary>
		public int GetIndexOfFirstVisibleLine()
        {
			int guessedLine = (int)(VerticalOffset / lineHeight);
			return guessedLine > totalLineCount ? totalLineCount : guessedLine;
		}

		/// <summary>
		/// Returns the index of the last visible text line.
		/// </summary>
		public int GetIndexOfLastVisibleLine()
        {
			double height = VerticalOffset + ViewportHeight;
			int guessedLine = (int)(height / lineHeight);
			return guessedLine > totalLineCount - 1 ? totalLineCount - 1 : guessedLine;
		}

		/// <summary>
		/// Formats and Highlights the text of a block.
		/// </summary>
		private void FormatBlock(InnerTextBlock currentBlock, InnerTextBlock previousBlock)
        {
			currentBlock.FormattedText = GetFormattedText(currentBlock.RawText);
            Dispatcher.Invoke(() => {
                CodeEditorContent.Instance.Highlight(currentBlock.FormattedText, 
                    openBracketPos, closeBracketPos, BracketBrush);
                currentBlock.Code = -1;
            });
        }

        /// <summary>
        /// Returns a formatted text object from the given string
        /// </summary>
        private FormattedText GetFormattedText(string text)
        {
			FormattedText ft = new FormattedText(
				text,
				System.Globalization.CultureInfo.InvariantCulture,
				FlowDirection.LeftToRight,
				new Typeface(FontFamily, FontStyle, FontWeight, FontStretch),
				FontSize,
				Brushes.White);
			ft.Trimming = TextTrimming.None;
			ft.LineHeight = lineHeight;
			return ft;
		}

		/// <summary>
		/// Returns a string containing a list of numbers separated with newlines.
		/// </summary>
		private FormattedText GetFormattedLineNumbers(int firstIndex, int lastIndex)
        {
			string text = "";
			for (int i = firstIndex + 1; i <= lastIndex + 1; i++)
            {
                text += i.ToString() + "\n";
            }
			text = text.Trim();
			FormattedText ft = new FormattedText(
				text,
				System.Globalization.CultureInfo.InvariantCulture,
				FlowDirection.LeftToRight,
				new Typeface(FontFamily, FontStyle, FontWeight, FontStretch),
				FontSize,
				new SolidColorBrush(Color.FromRgb(0x21, 0xA1, 0xD8)));
			ft.Trimming = TextTrimming.None;
			ft.LineHeight = lineHeight;
			ft.TextAlignment = TextAlignment.Right;
			return ft;
		}

		/// <summary>
		/// Returns the width of a text once formatted.
		/// </summary>
		private double GetFormattedTextWidth(string text)
        {
			FormattedText ft = new FormattedText(
				text,
				System.Globalization.CultureInfo.InvariantCulture,
				FlowDirection.LeftToRight,
				new Typeface(FontFamily, FontStyle, FontWeight, FontStretch),
				FontSize,
				Brushes.White);
			ft.Trimming = TextTrimming.None;
			ft.LineHeight = lineHeight;
			return ft.Width;
		}

        #endregion
    }
}
