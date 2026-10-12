/** @type {import('next').NextConfig} */
const isDevelopment = process.env.NODE_ENV === "development";

const nextConfig = {
  output: "export",
  trailingSlash: true,
  assetPrefix: isDevelopment ? undefined : "./",
  devIndicators: false,
  images: {
    unoptimized: true
  },
  webpack: (config, { dev }) => {
    if (dev) {
      config.cache = false;
    }

    return config;
  }
};

export default nextConfig;
