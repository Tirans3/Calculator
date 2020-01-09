using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace Calculator
{
    public partial class MainWindow : Window
    {
        private  string leftValue = "";

        private  string rightValue = "";

        private  string action = "";

        public MainWindow()
        {
            InitializeComponent();

            foreach(UIElement button in Root.Children)
            {
                if (button is Button) ((Button)button).Click += Button_Click;
            }
        }

        private void Button_Click(object sender,RoutedEventArgs e)
        {
            string s = ((Button)e.OriginalSource).Content.ToString();

            textblock.Text += s;

            int num;

            bool result = int.TryParse(s, out num);

            if(result==true)
            {
                if(string.IsNullOrWhiteSpace(action))
                {
                    leftValue += s;
                }
                else
                {
                    rightValue += s;
                }
            }
            else if(s == "=")
            {
                    Action();
                    textblock.Text = leftValue;
                    rightValue = "";
                    action = "";
            }
            else if(s=="CLEAR")
            {
                leftValue = "";
                rightValue = "";
                action = "";
                textblock.Text = "";
            }
            else if(!string.IsNullOrWhiteSpace(action))
            {
                Action();
                action = s;
                rightValue = "";
            }
            else
            {
                action = s;
            }
            
        }

        private void Action()
        {
            int left = int.Parse(leftValue);

            int right = int.Parse(rightValue);

            switch(action)
            {
                case "+":leftValue = (left + right).ToString();
                    break;
                case "-":leftValue = (left - right).ToString();
                    break;
                case "*":leftValue = (left * right).ToString();
                    break;
                case "/":leftValue = (left / right).ToString();
                    break;
            }

        }
    }
}
