plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.plugin.compose")
}

android {
    namespace = "app.relaxkonos.mobile"
    compileSdk = 36

    defaultConfig {
        applicationId = "app.relaxkonos.mobile"
        minSdk = 23
        targetSdk = 36
        versionCode = 1
        versionName = "0.2.0-v1a"
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }

    androidResources {
        // Language-level, not region-level: the app ships one Chinese translation, and `zh` matches
        // every Chinese variant the platform may report, including Traditional-script ones.
        localeFilters += listOf("en", "zh", "ja")
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
        }
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }
}

dependencies {
    implementation(platform("androidx.compose:compose-bom:2025.12.01"))
    implementation("androidx.activity:activity-compose:1.12.2")
    implementation("androidx.appcompat:appcompat:1.7.1")
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.ui:ui-tooling-preview")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.material:material-icons-core")
    implementation("androidx.fragment:fragment:1.9.0")
    implementation("androidx.biometric:biometric:1.1.0")
    // 2.11.0 and later declare minCompileSdk 37; this module compiles against 36, so the newest
    // release that still declares minCompileSdk 35 is pinned. Revisit when compileSdk moves to 37.
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.10.0")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.10.2")

    // 服务器中心：内置 SSH/SFTP 传输，替代对系统 ssh/scp/sftp 工具的依赖。
    // mwiede/jsch 是 com.jcraft:jsch 的维护分支，支持 rsa-sha2-256/512 等现代算法；
    // bcprov 提供 ed25519 / curve25519 / chacha20-poly1305 所需的密码学原语（jsch 直接调用其轻量 API）。
    implementation("com.github.mwiede:jsch:2.28.7")
    implementation("org.bouncycastle:bcprov-jdk18on:1.80.2")

    debugImplementation("androidx.compose.ui:ui-tooling")
    testImplementation("junit:junit:4.13.2")
    testImplementation("org.jetbrains.kotlinx:kotlinx-coroutines-test:1.10.2")
    androidTestImplementation(platform("androidx.compose:compose-bom:2025.12.01"))
    androidTestImplementation("androidx.test.ext:junit:1.3.0")
    androidTestImplementation("androidx.test.espresso:espresso-core:3.7.0")
    androidTestImplementation("androidx.compose.ui:ui-test-junit4")
    debugImplementation("androidx.compose.ui:ui-test-manifest")
}
