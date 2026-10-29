import type { Metadata } from "next";
import "./globals.css";

export const metadata: Metadata = {
  title: "Minh Huy AI Office",
  description: "Multi-tenant AI Office for ERP and accounting operations",
};

// Applies the saved light/dark choice before first paint so the page never flashes.
const themeScript = `try{var t=localStorage.getItem("aioffice-theme");if(t!=="light"&&t!=="dark")t=matchMedia("(prefers-color-scheme: dark)").matches?"dark":"light";document.documentElement.classList.toggle("dark",t==="dark")}catch(e){}`;

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="vi" suppressHydrationWarning>
      <head>
        <script dangerouslySetInnerHTML={{ __html: themeScript }} />
      </head>
      <body>{children}</body>
    </html>
  );
}
