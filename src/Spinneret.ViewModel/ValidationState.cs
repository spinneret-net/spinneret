using System.ComponentModel;

namespace Spinneret.ViewModel
{
    public interface IValidationState : INotifyPropertyChanged
    {
        void RegisterBoundProperty(string key);
        string? GetError(string key);
        void AddError(string key, string error);
        void RemoveError(string key);
        void ClearErrors();
        public bool HasErrors { get; }
        IEnumerable<(string Key, string Error)> Errors { get; }
        IEnumerable<(string Key, string Error)> BoundErrors { get; }
        IEnumerable<(string Key, string Error)> UnboundErrors { get; }
    }

    public class ValidationState : BindableBase, IValidationState
    {
        private readonly HashSet<string> _boundProperties = [];
        private readonly Dictionary<string, string> _errors = new();

        public bool HasErrors => _errors.Count > 0;
        
        public IEnumerable<(string Key, string Error)> Errors => _errors.Select(kv => (kv.Key, kv.Value));

        public IEnumerable<(string Key, string Error)> BoundErrors => 
            _errors.Where(kv => _boundProperties.Contains(kv.Key))
                   .Select(kv => (kv.Key, kv.Value));

        public IEnumerable<(string Key, string Error)> UnboundErrors =>
            _errors.Where(kv => !_boundProperties.Contains(kv.Key))
                   .Select(kv => (kv.Key, kv.Value));

        public void RegisterBoundProperty(string key)
        {
            if (!_boundProperties.Add(key) || !_errors.ContainsKey(key)) return;
            
            RaisePropertyChanged(nameof(BoundErrors));
            RaisePropertyChanged(nameof(UnboundErrors));
        }

        public string? GetError(string key)
        {
            return _errors.GetValueOrDefault(key);
        }

        public void AddError(string key, string error)
        {
            if (_errors.TryGetValue(key, out var existing) && existing == error) return;

            var errorCountBefore = _errors.Count;
            
            _errors[key] = error;

            RaisePropertyChanged(nameof(Errors));
            RaisePropertyChanged(_boundProperties.Contains(key) ? nameof(BoundErrors) : nameof(UnboundErrors));

            if (errorCountBefore == 0)
            {
                RaisePropertyChanged(nameof(HasErrors));
            }
        }

        public void RemoveError(string key)
        {
            if (!_errors.Remove(key)) return;
            RaisePropertyChanged(nameof(Errors));
            RaisePropertyChanged(_boundProperties.Contains(key) ? nameof(BoundErrors) : nameof(UnboundErrors));
            
            if (_errors.Count == 0)
            {
                RaisePropertyChanged(nameof(HasErrors));
            }
        }

        public void ClearErrors()
        {
            var errorCount = _errors.Count;
            if (errorCount == 0) return;
            
            var boundErrorCount = _boundProperties.Count(x => _errors.ContainsKey(x));
            var unboundErrorCount = errorCount - boundErrorCount;
            
            _errors.Clear();
            RaisePropertyChanged(nameof(Errors));

            if (boundErrorCount > 0)
            {
                RaisePropertyChanged(nameof(BoundErrors));
            }

            if (unboundErrorCount > 0)
            {
                RaisePropertyChanged(nameof(UnboundErrors));
            }
            
            RaisePropertyChanged(nameof(HasErrors));
        }
    }
}
