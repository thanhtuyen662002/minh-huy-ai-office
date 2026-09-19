import type { Metadata } from "next";
import "./globals.css";

export const metadata: Metadata = {
  title: "Minh Huy AI Office",
  description: "Multi-tenant AI Office for ERP and accounting operations",
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="vi">
      <body>{children}</body>
    </html>
  );
}
