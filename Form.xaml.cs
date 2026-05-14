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

namespace DAN_Plugin
{
    /// <summary>
    /// Логика взаимодействия для Form.xaml
    /// </summary>
    public partial class Form : Window
    {

       public bool  formExecute = false;

        public Form(bool FromMainExecute)
        {
            InitializeComponent();

            formExecute = FromMainExecute;
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
         formExecute = true;
         this.Close();
        }

        
        private void Cancel_Button_Click(object sender, RoutedEventArgs e)
        {
            formExecute = false;
            this.Close();
        }


        private void Schedule_SA_Checked(object sender, RoutedEventArgs e)
        {
            
        }

        private void Schedule_VD_Checked(object sender, RoutedEventArgs e)
        {
            
        }

        private void Schedule_VRS_Checked(object sender, RoutedEventArgs e)
        {
           
        }

        private void Schedule_VM_Checked(object sender, RoutedEventArgs e)
        {

        }
    }
}
