cmake_minimum_required(VERSION 3.16)
project(StardustSimNative CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
if(NOT CMAKE_BUILD_TYPE)
  set(CMAKE_BUILD_TYPE Release)
endif()

# RocketSim (MIT, https://github.com/ZealanL/RocketSim). Point ROCKETSIM_SOURCE_DIR at a local
# checkout to build offline; otherwise the pinned revision is fetched.
set(ROCKETSIM_REVISION c2baacb8f4b441dd8505e63c2aeb5a1679b60b02 CACHE STRING "RocketSim git revision")
if(ROCKETSIM_SOURCE_DIR)
  set(rocketsim_SOURCE_DIR ${ROCKETSIM_SOURCE_DIR})
else()
  include(FetchContent)
  FetchContent_Declare(rocketsim
    GIT_REPOSITORY https://github.com/ZealanL/RocketSim.git
    GIT_TAG ${ROCKETSIM_REVISION}
    GIT_SHALLOW FALSE)
  FetchContent_GetProperties(rocketsim)
  if(NOT rocketsim_POPULATED)
    FetchContent_Populate(rocketsim)
  endif()
endif()

file(GLOB_RECURSE ROCKETSIM_SOURCES
  "${rocketsim_SOURCE_DIR}/src/*.cpp"
  "${rocketsim_SOURCE_DIR}/libsrc/bullet3-3.24/*.cpp")

add_library(rocketsim_static STATIC ${ROCKETSIM_SOURCES})
target_include_directories(rocketsim_static PUBLIC "${rocketsim_SOURCE_DIR}/src")
if(NOT MSVC)
  target_compile_options(rocketsim_static PRIVATE -w -O3)
endif()

add_library(rsbridge SHARED src/rsbridge.cpp src/arena_mesh.cpp)
target_link_libraries(rsbridge PRIVATE rocketsim_static)
target_include_directories(rsbridge PRIVATE "${rocketsim_SOURCE_DIR}/src" src)
if(NOT MSVC)
  target_compile_options(rsbridge PRIVATE -O3 -Wall)
  set_target_properties(rsbridge PROPERTIES CXX_VISIBILITY_PRESET hidden)
endif()
