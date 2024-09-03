
namespace Memorize
{
    public static class Navigator
    {
        private static bool _didInit;
        private static NavigationPage? _navPage;

        public static void Init(NavigationPage navPage)
        {
            _didInit = true;
            _navPage = navPage;
        }

        public static void Navigate(string page)
        {
            if (!_didInit) throw new Exception("Navigator not initialized");
            _navPage!.Navigate(page);
        }
    }
}
