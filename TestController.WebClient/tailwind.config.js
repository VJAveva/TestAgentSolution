/** @type {import('tailwindcss').Config} */
export default {
  content: ['./index.html', './src/**/*.{ts,tsx}'],
  theme: {
    extend: {
      colors: {
        bg: { DEFAULT: '#1E1E2E', card: '#313244', panel: '#181825', surface: '#45475A', ribbon: '#1B1B2F' },
        accent: '#89B4FA',
        'acc-green': '#A6E3A1',
        'acc-red': '#F38BA8',
        'acc-yellow': '#F9E2AF',
        'acc-peach': '#FAB387',
        'acc-mauve': '#CBA6F7',
        'acc-blue': '#3B82F6',
        text: { primary: '#CDD6F4', secondary: '#9399B2', muted: '#585B70' },
        bdr: '#585B70',
      },
      fontFamily: {
        sans: ['Segoe UI', 'Arial', 'sans-serif'],
      },
    },
  },
  plugins: [],
};
