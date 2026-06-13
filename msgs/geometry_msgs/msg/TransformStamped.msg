# This expresses a transform from coordinate frame header.frame_id
# to the coordinate frame child_frame_id at the time of header.stamp
#
# This message is mostly used by the
# <a href="https://index.ros.org/p/tf2/">tf2</a> package.
# See its documentation for more information.
#
# The child_frame_id is necessary in addition to the header's frame_id
# because the header represents the time and reference frame of the data,
# while child_frame_id is the coordinate frame of the transform.

std_msgs/Header header

# the frame id of the child frame
string child_frame_id

Transform transform
