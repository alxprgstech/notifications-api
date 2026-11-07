
try {
  const preference = localStorage.getItem('notify-theme');
  if (preference === 'light' || preference === 'dark') {
    document.documentElement.dataset.theme = preference;
  }
} catch {
  // The CSS follows the system theme when browser storage is unavailable.
}
