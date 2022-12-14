using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.ComponentModel;
using System.Windows.Input;
using Microsoft.Win32;
using System.Windows;
using NAudio.Wave;
using NAudioWpfDemo.AudioPlaybackDemo;

namespace NAudioWpfDemo
{
    class AudioPlaybackViewModel : INotifyPropertyChanged, IDisposable
    {
        private AudioPlayback audioPlayback;
        private List<IVisualizationPlugin> visualizations;
        private IVisualizationPlugin selectedVisualization;
        private string selectedFile;

        public ICommand OpenFileCommand { get; private set; }
        public ICommand PlayCommand { get; private set; }
        public ICommand PauseCommand { get; private set; }
        public ICommand StopCommand { get; private set; }

        public AudioPlaybackViewModel(IEnumerable<IVisualizationPlugin> visualizations)
        {
            this.visualizations = new List<IVisualizationPlugin>(visualizations);
            this.selectedVisualization = this.visualizations.FirstOrDefault();

            this.audioPlayback = new AudioPlayback();
            audioPlayback.MaximumCalculated += new EventHandler<MaxSampleEventArgs>(audioGraph_MaximumCalculated);
            audioPlayback.FftCalculated += new EventHandler<FftEventArgs>(audioGraph_FftCalculated);

            PlayCommand = new RelayCommand(
                        () => this.Play(),
                        () => true);
            OpenFileCommand = new RelayCommand(
                        () => this.OpenFile(),
                        () => true);
            StopCommand = new RelayCommand(
                        () => this.Stop(),
                        () => true);
            PauseCommand = new RelayCommand(
                        () => this.Pause(),
                        () => true);
        }

        private void Pause()
        {
            audioPlayback.Pause();
        }

        public IList<IVisualizationPlugin> Visualizations { get { return this.visualizations; } }

        public IVisualizationPlugin SelectedVisualization
        {
            get
            {
                return this.selectedVisualization;
            }
            set
            {
                if (this.selectedVisualization != value)
                {
                    this.selectedVisualization = value;
                    RaisePropertyChangedEvent("SelectedVisualization");
                    RaisePropertyChangedEvent("Visualization");
                }
            }
        }

        public object Visualization
        {
            get
            {
                return this.selectedVisualization.Content;
            }
        }

        void audioGraph_FftCalculated(object sender, FftEventArgs e)
        {
            if (this.SelectedVisualization != null)
            {
                this.SelectedVisualization.OnFftCalculated(e.Result);
            }
        }

        void audioGraph_MaximumCalculated(object sender, MaxSampleEventArgs e)
        {
            if (this.SelectedVisualization != null)
            {
                this.SelectedVisualization.OnMaxCalculated(e.MinSample, e.MaxSample);
            }
        }

        private void OpenFile()
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "All Supported Files (*.wav;*.mp3)|*.wav;*.mp3|All Files (*.*)|*.*";
            bool? result = openFileDialog.ShowDialog();
            if (result.HasValue && result.Value)
            {
                this.selectedFile = openFileDialog.FileName;
                audioPlayback.Load(this.selectedFile);
            }
        }

        private void Play()
        {
            if (this.selectedFile == null)
            {
                OpenFile();
            }
            if (this.selectedFile != null)
            {
                audioPlayback.Play();
            }
        }

        private void Stop()
        {
            audioPlayback.Stop();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void RaisePropertyChangedEvent(string propertyName)
        {
            if (PropertyChanged != null)
            {
                PropertyChanged(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        public void Dispose()
        {
            audioPlayback.Dispose();
        }
    }
}
