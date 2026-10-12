import "./globals.css";

export const metadata = {
  title: "Indoor Report Generator",
  description: "Professional indoor network report generator by Vinfocom",
  icons: {
    icon: [
      { url: "/favicon.ico" },
      { url: "/toollogo.svg", type: "image/svg+xml" }
    ],
    shortcut: "/favicon.ico",
    apple: "/toollogo.svg"
  }
};

export default function RootLayout({ children }) {
  return (
    <html lang="en">
      <body>{children}</body>
    </html>
  );
}
